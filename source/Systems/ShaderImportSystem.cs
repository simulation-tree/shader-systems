using Collections;
using Data;
using Data.Components;
using Shaders.Components;
using Simulation;
using System;
using System.Diagnostics;
using Unmanaged;
using Worlds;

namespace Shaders.Systems
{
    public readonly partial struct ShaderImportSystem : ISystem
    {
        private readonly ShaderCompiler shaderCompiler;
        private readonly Dictionary<Entity, uint> shaderVersions;
        private readonly List<Operation> operations;

        private ShaderImportSystem(ShaderCompiler shaderCompiler, Dictionary<Entity, uint> shaderVersions, List<Operation> operations)
        {
            this.shaderCompiler = shaderCompiler;
            this.shaderVersions = shaderVersions;
            this.operations = operations;
        }

        void ISystem.Start(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                ShaderCompiler shaderCompiler = new();
                Dictionary<Entity, uint> shaderVersions = new();
                List<Operation> operations = new();
                systemContainer.Write(new ShaderImportSystem(shaderCompiler, shaderVersions, operations));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            ComponentQuery<IsShaderRequest> requestQuery = new(world);
            foreach (var r in requestQuery)
            {
                ref IsShaderRequest request = ref r.component1;
                bool sourceChanged;
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
                    if (TryLoadShader(shader, request))
                    {
                        shaderVersions.AddOrSet(shader, request.version);
                    }
                }
            }

            PerformOperations(world);
        }

        void ISystem.Finish(in SystemContainer systemContainer, in World world)
        {
            if (systemContainer.World == world)
            {
                while (operations.Count > 0)
                {
                    Operation operation = operations.RemoveAt(0);
                    operation.Dispose();
                }

                operations.Dispose();
                shaderCompiler.Dispose();
                shaderVersions.Dispose();
            }
        }

        private readonly void PerformOperations(World world)
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
        private readonly bool TryLoadShader(Entity shader, IsShaderRequest request)
        {
            World world = shader.GetWorld();
            DataRequest vertex = new(world, shader.GetReference(request.vertex));
            DataRequest fragment = new(world, shader.GetReference(request.fragment));
            while (!vertex.Is() || !fragment.Is())
            {
                Trace.WriteLine($"Waiting for shader request `{shader}` to have data available");
                //todo: fault: if data update performs after shader update, then this may never break, kinda scary
                //Console.WriteLine("hanging shaders");
                //Thread.Sleep(1);
                return false;
            }

            Trace.WriteLine($"Starting shader compilation for `{shader}`");
            USpan<BinaryData> spvVertex = shaderCompiler.GLSLToSPV(vertex.Data, ShaderStage.Vertex).As<BinaryData>();
            USpan<BinaryData> spvFragment = shaderCompiler.GLSLToSPV(fragment.Data, ShaderStage.Fragment).As<BinaryData>();

            Operation operation = new();
            Operation.SelectedEntity selectedEntity;
            ref IsShader component = ref shader.TryGetComponent<IsShader>(out bool contains);
            if (contains)
            {
                uint existingVertex = shader.GetReference(component.vertex);
                uint existingFragment = shader.GetReference(component.fragment);

                selectedEntity = operation.SelectEntity(existingVertex);
                selectedEntity.ResizeArray<BinaryData>(spvVertex.Length);
                selectedEntity.SetArrayElements(0, spvVertex);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(existingFragment);
                selectedEntity.ResizeArray<BinaryData>(spvFragment.Length);
                selectedEntity.SetArrayElements(0, spvFragment);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(shader);
                selectedEntity.SetComponent(new IsShader(component.vertex, component.fragment, component.version + 1));
            }
            else
            {
                selectedEntity = operation.CreateEntity();
                selectedEntity.CreateArray(spvVertex);

                selectedEntity = operation.CreateEntity();
                selectedEntity.CreateArray(spvFragment);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(shader);
                selectedEntity.AddReferenceTowardsPreviouslyCreatedEntity(1); //for vertex
                selectedEntity.AddReferenceTowardsPreviouslyCreatedEntity(0); //for fragment

                uint referenceCount = shader.GetReferenceCount();
                selectedEntity.AddComponent(new IsShader((rint)(referenceCount + 1), (rint)(referenceCount + 2)));
            }

            using List<ShaderPushConstant> pushConstants = new();
            using List<ShaderUniformProperty> uniformProperties = new();
            using List<ShaderUniformPropertyMember> uniformPropertyMembers = new();
            using List<ShaderSamplerProperty> textureProperties = new();
            using List<ShaderVertexInputAttribute> vertexInputAttributes = new();

            //fill in shader data
            shaderCompiler.ReadPushConstantsFromSPV(spvVertex.As<byte>(), pushConstants);
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex.As<byte>(), uniformProperties, uniformPropertyMembers);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment.As<byte>(), textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex.As<byte>(), vertexInputAttributes);

            //make sure lists for shader properties exists
            if (!shader.ContainsArray<ShaderPushConstant>())
            {
                selectedEntity.CreateArray(pushConstants.AsSpan());
            }
            else
            {
                selectedEntity.ResizeArray<ShaderPushConstant>(pushConstants.Count);
                selectedEntity.SetArrayElements(0, pushConstants.AsSpan());
            }

            if (!shader.ContainsArray<ShaderUniformProperty>())
            {
                selectedEntity.CreateArray(uniformProperties.AsSpan());
            }
            else
            {
                selectedEntity.ResizeArray<ShaderUniformProperty>(uniformProperties.Count);
                selectedEntity.SetArrayElements(0, uniformProperties.AsSpan());
            }

            if (!shader.ContainsArray<ShaderUniformPropertyMember>())
            {
                selectedEntity.CreateArray(uniformPropertyMembers.AsSpan());
            }
            else
            {
                selectedEntity.ResizeArray<ShaderUniformPropertyMember>(uniformPropertyMembers.Count);
                selectedEntity.SetArrayElements(0, uniformPropertyMembers.AsSpan());
            }

            if (!shader.ContainsArray<ShaderSamplerProperty>())
            {
                selectedEntity.CreateArray(textureProperties.AsSpan());
            }
            else
            {
                selectedEntity.ResizeArray<ShaderSamplerProperty>(textureProperties.Count);
                selectedEntity.SetArrayElements(0, textureProperties.AsSpan());
            }

            if (!shader.ContainsArray<ShaderVertexInputAttribute>())
            {
                selectedEntity.CreateArray(vertexInputAttributes.AsSpan());
            }
            else
            {
                selectedEntity.ResizeArray<ShaderVertexInputAttribute>(vertexInputAttributes.Count);
                selectedEntity.SetArrayElements(0, vertexInputAttributes.AsSpan());
            }

            operations.Add(operation);
            Trace.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
            return true;
        }
    }
}
