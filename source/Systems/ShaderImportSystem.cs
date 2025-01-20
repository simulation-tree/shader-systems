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
        private readonly Stack<Operation> operations;

        private ShaderImportSystem(ShaderCompiler shaderCompiler, Dictionary<Entity, uint> shaderVersions, Stack<Operation> operations)
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
                Stack<Operation> operations = new();
                systemContainer.Write(new ShaderImportSystem(shaderCompiler, shaderVersions, operations));
            }
        }

        void ISystem.Update(in SystemContainer systemContainer, in World world, in TimeSpan delta)
        {
            Schema schema = world.Schema;
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
                    if (TryLoadShader(shader, request, schema))
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
                while (operations.TryPop(out Operation operation))
                {
                    operation.Dispose();
                }

                operations.Dispose();
                shaderCompiler.Dispose();
                shaderVersions.Dispose();
            }
        }

        private readonly void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                world.Perform(operation);
                operation.Dispose();
            }
        }

        /// <summary>
        /// Updates the shader entity with up to date <see cref="ShaderUniformProperty"/>,
        /// <see cref="ShaderSamplerProperty"/>, and <see cref="ShaderVertexInputAttribute"/> collections.
        /// <para>Modifies the `byte` lists to contain SPV bytecode.</para>
        /// </summary>
        private readonly bool TryLoadShader(Entity shader, IsShaderRequest request, Schema schema)
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
                selectedEntity.ResizeArray<BinaryData>(spvVertex.Length, schema);
                selectedEntity.SetArrayElements(0, spvVertex, schema);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(existingFragment);
                selectedEntity.ResizeArray<BinaryData>(spvFragment.Length, schema);
                selectedEntity.SetArrayElements(0, spvFragment, schema);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(shader);
                selectedEntity.SetComponent(new IsShader(component.vertex, component.fragment, component.version + 1), schema);
            }
            else
            {
                selectedEntity = operation.CreateEntity();
                selectedEntity.CreateArray(spvVertex, schema);

                selectedEntity = operation.CreateEntity();
                selectedEntity.CreateArray(spvFragment, schema);

                operation.ClearSelection();
                selectedEntity = operation.SelectEntity(shader);
                selectedEntity.AddReferenceTowardsPreviouslyCreatedEntity(1); //for vertex
                selectedEntity.AddReferenceTowardsPreviouslyCreatedEntity(0); //for fragment

                uint referenceCount = shader.GetReferenceCount();
                selectedEntity.AddComponent(new IsShader((rint)(referenceCount + 1), (rint)(referenceCount + 2)), schema);
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
                selectedEntity.CreateArray(pushConstants.AsSpan(), schema);
            }
            else
            {
                selectedEntity.ResizeArray<ShaderPushConstant>(pushConstants.Count, schema);
                selectedEntity.SetArrayElements(0, pushConstants.AsSpan(), schema);
            }

            if (!shader.ContainsArray<ShaderUniformProperty>())
            {
                selectedEntity.CreateArray(uniformProperties.AsSpan(), schema);
            }
            else
            {
                selectedEntity.ResizeArray<ShaderUniformProperty>(uniformProperties.Count, schema);
                selectedEntity.SetArrayElements(0, uniformProperties.AsSpan(), schema);
            }

            if (!shader.ContainsArray<ShaderUniformPropertyMember>())
            {
                selectedEntity.CreateArray(uniformPropertyMembers.AsSpan(), schema);
            }
            else
            {
                selectedEntity.ResizeArray<ShaderUniformPropertyMember>(uniformPropertyMembers.Count, schema);
                selectedEntity.SetArrayElements(0, uniformPropertyMembers.AsSpan(), schema);
            }

            if (!shader.ContainsArray<ShaderSamplerProperty>())
            {
                selectedEntity.CreateArray(textureProperties.AsSpan(), schema);
            }
            else
            {
                selectedEntity.ResizeArray<ShaderSamplerProperty>(textureProperties.Count, schema);
                selectedEntity.SetArrayElements(0, textureProperties.AsSpan(), schema);
            }

            if (!shader.ContainsArray<ShaderVertexInputAttribute>())
            {
                selectedEntity.CreateArray(vertexInputAttributes.AsSpan(), schema);
            }
            else
            {
                selectedEntity.ResizeArray<ShaderVertexInputAttribute>(vertexInputAttributes.Count, schema);
                selectedEntity.SetArrayElements(0, vertexInputAttributes.AsSpan(), schema);
            }

            operations.Push(operation);
            Trace.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
            return true;
        }
    }
}
