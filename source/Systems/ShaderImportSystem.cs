using Collections;
using Data;
using Shaders.Components;
using Simulation;
using Simulation.Functions;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unmanaged;

namespace Shaders.Systems
{
    public readonly struct ShaderImportSystem : ISystem
    {
        private readonly ComponentQuery<IsShaderRequest> requestsQuery;
        private readonly ComponentQuery<IsShader> shaderQuery;
        private readonly ShaderCompiler shaderCompiler;
        private readonly Dictionary<Entity, uint> shaderVersions;
        private readonly List<Operation> operations;

        readonly unsafe InitializeFunction ISystem.Initialize => new(&Initialize);
        readonly unsafe IterateFunction ISystem.Iterate => new(&Update);
        readonly unsafe FinalizeFunction ISystem.Finalize => new(&Finalize);

        [UnmanagedCallersOnly]
        private static void Initialize(SystemContainer container, World world)
        {
        }

        [UnmanagedCallersOnly]
        private static void Update(SystemContainer container, World world, TimeSpan delta)
        {
            ref ShaderImportSystem system = ref container.Read<ShaderImportSystem>();
            system.Update(world);
        }

        [UnmanagedCallersOnly]
        private static void Finalize(SystemContainer container, World world)
        {
            if (container.World == world)
            {
                ref ShaderImportSystem system = ref container.Read<ShaderImportSystem>();
                system.CleanUp();
            }
        }

        public ShaderImportSystem()
        {
            requestsQuery = new();
            shaderQuery = new();
            shaderCompiler = new();
            shaderVersions = new();
            operations = new();
        }

        private void CleanUp()
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                operation.Dispose();
            }

            operations.Dispose();
            shaderCompiler.Dispose();
            shaderQuery.Dispose();
            requestsQuery.Dispose();
            shaderVersions.Dispose();
        }

        private void Update(World world)
        {
            ImportShaders(world);
            PerformOperations(world);
        }

        private void ImportShaders(World world)
        {
            requestsQuery.Update(world);
            foreach (var r in requestsQuery)
            {
                IsShaderRequest request = r.Component1;
                bool sourceChanged = false;
                Entity shader = new(world, r.entity);
                if (!shaderVersions.ContainsKey(shader))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = shaderVersions[shader] != request.version;
                }

                if (sourceChanged)
                {
                    if (TryImportShaderDataOntoEntity((shader, request)))
                    {
                        shaderVersions.AddOrSet(shader, request.version);
                    }
                }
            }
        }

        private void PerformOperations(World world)
        {
            while (operations.Count > 0)
            {
                Operation operation = operations.RemoveAt(0);
                world.Perform(operation);
                operation.Dispose();
            }
        }

        /// <summary>
        /// Updates the shader entity with up to date <see cref="ShaderUniformProperty"/>,
        /// <see cref="ShaderSamplerProperty"/>, and <see cref="ShaderVertexInputAttribute"/> collections.
        /// <para>Modifies the `byte` lists to contain SPV bytecode.</para>
        /// </summary>
        private bool TryImportShaderDataOntoEntity((Entity shader, IsShaderRequest request) input)
        {
            Entity shader = input.shader;
            World world = shader.GetWorld();
            IsShaderRequest request = input.request;
            DataRequest vertex = new(world, shader.GetReference(request.vertex));
            DataRequest fragment = new(world, shader.GetReference(request.fragment));
            while (!vertex.IsCompliant() || !fragment.IsCompliant())
            {
                Trace.WriteLine($"Waiting for shader request `{shader}` to have data available");
                //todo: fault: if data update performs after shader update, then this may never break, kinda scary
                //Console.WriteLine("hanging shaders");
                //Thread.Sleep(1);
                return false;
            }

            Trace.WriteLine($"Starting shader compilation for `{shader}`");
            USpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertex.Data, ShaderStage.Vertex);
            USpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragment.Data, ShaderStage.Fragment);

            Operation operation = new();
            if (shader.TryGetComponent(out IsShader component))
            {
                uint existingVertex = shader.GetReference(component.vertex);
                uint existingFragment = shader.GetReference(component.fragment);

                operation.SelectEntity(existingVertex);
                operation.ResizeArray<byte>(spvVertex.Length);
                operation.SetArrayElements(0, spvVertex);
                operation.ClearSelection();

                operation.SelectEntity(existingFragment);
                operation.ResizeArray<byte>(spvFragment.Length);
                operation.SetArrayElements(0, spvFragment);
                operation.ClearSelection();

                component.version++;
                operation.SelectEntity(shader);
                operation.SetComponent(component);
            }
            else
            {
                operation.CreateEntity();
                operation.CreateArray<byte>(spvVertex);
                operation.ClearSelection();

                operation.CreateEntity();
                operation.CreateArray<byte>(spvFragment);
                operation.ClearSelection();

                operation.SelectEntity(shader);
                operation.AddReferenceTowardsPreviouslyCreatedEntity(1); //for vertex
                operation.AddReferenceTowardsPreviouslyCreatedEntity(0); //for fragment

                uint referenceCount = shader.GetReferenceCount();
                operation.AddComponent(new IsShader((rint)(referenceCount + 1), (rint)(referenceCount + 2)));
            }

            using List<ShaderPushConstant> pushConstants = new();
            using List<ShaderUniformProperty> uniformProperties = new();
            using List<ShaderUniformPropertyMember> uniformPropertyMembers = new();
            using List<ShaderSamplerProperty> textureProperties = new();
            using List<ShaderVertexInputAttribute> vertexInputAttributes = new();

            //fill in shader data
            shaderCompiler.ReadPushConstantsFromSPV(spvVertex, pushConstants);
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex, uniformProperties, uniformPropertyMembers);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment, textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex, vertexInputAttributes);

            //make sure lists for shader properties exists
            if (!shader.ContainsArray<ShaderPushConstant>())
            {
                operation.CreateArray<ShaderPushConstant>(pushConstants.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderPushConstant>(pushConstants.Count);
                operation.SetArrayElements(0, pushConstants.AsSpan());
            }

            if (!shader.ContainsArray<ShaderUniformProperty>())
            {
                operation.CreateArray<ShaderUniformProperty>(uniformProperties.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderUniformProperty>(uniformProperties.Count);
                operation.SetArrayElements(0, uniformProperties.AsSpan());
            }

            if (!shader.ContainsArray<ShaderUniformPropertyMember>())
            {
                operation.CreateArray<ShaderUniformPropertyMember>(uniformPropertyMembers.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderUniformPropertyMember>(uniformPropertyMembers.Count);
                operation.SetArrayElements(0, uniformPropertyMembers.AsSpan());
            }

            if (!shader.ContainsArray<ShaderSamplerProperty>())
            {
                operation.CreateArray<ShaderSamplerProperty>(textureProperties.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderSamplerProperty>(textureProperties.Count);
                operation.SetArrayElements(0, textureProperties.AsSpan());
            }

            if (!shader.ContainsArray<ShaderVertexInputAttribute>())
            {
                operation.CreateArray<ShaderVertexInputAttribute>(vertexInputAttributes.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderVertexInputAttribute>(vertexInputAttributes.Count);
                operation.SetArrayElements(0, vertexInputAttributes.AsSpan());
            }

            operations.Add(operation);
            Trace.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
            return true;
        }
    }
}
