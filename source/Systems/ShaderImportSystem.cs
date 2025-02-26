using Collections.Generic;
using Data.Messages;
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
            Simulator simulator = systemContainer.simulator;
            ComponentType componentType = world.Schema.GetComponent<IsShaderRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.Contains(componentType))
                {
                    USpan<uint> entities = chunk.Entities;
                    USpan<IsShaderRequest> components = chunk.GetComponents<IsShaderRequest>(componentType);
                    for (uint i = 0; i < entities.Length; i++)
                    {
                        ref IsShaderRequest request = ref components[i];
                        Entity shader = new(world, entities[i]);
                        if (request.status == IsShaderRequest.Status.Submitted)
                        {
                            request.status = IsShaderRequest.Status.Loading;
                            Trace.WriteLine($"Started searching data for shader `{shader}` with address `{request.address}`");
                        }

                        if (request.status == IsShaderRequest.Status.Loading)
                        {
                            IsShaderRequest dataRequest = request;
                            if (TryLoadShader(shader, dataRequest, simulator))
                            {
                                Trace.WriteLine($"Shader `{shader}` has been loaded");

                                //todo: being done this way because reference to the request may have shifted
                                shader.SetComponent(dataRequest.BecomeLoaded());
                            }
                            else
                            {
                                request.duration += delta;
                                if (request.duration >= request.timeout)
                                {
                                    Trace.TraceError($"Shader `{shader}` could not be loaded");
                                    request.status = IsShaderRequest.Status.NotFound;
                                }
                            }
                        }
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
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private readonly bool TryLoadShader(Entity shader, IsShaderRequest request, Simulator simulator)
        {
            ThrowIfUnknownShaderType(request.type);

            LoadData message = new(shader.world, request.address);
            if (simulator.TryHandleMessage(ref message) != default)
            {
                if (message.IsLoaded)
                {
                    Trace.WriteLine($"Loading shader data onto entity `{shader}`");
                    USpan<byte> loadedBytes = message.Bytes;
                    USpan<byte> shaderBytes = shaderCompiler.GLSLToSPV(loadedBytes, request.type);
                    message.Dispose();

                    Operation operation = new();
                    operation.SelectEntity(shader);
                    shader.TryGetComponent(out IsShader component);
                    operation.AddOrSetComponent(component.IncrementVersion());
                    operation.CreateOrSetArray(shaderBytes.As<ShaderByte>());

                    //fill metadata
                    using List<ShaderUniformPropertyMember> uniformPropertyMembers = new();
                    using List<ShaderUniformProperty> uniformProperties = new();
                    shaderCompiler.ReadUniformPropertiesFromSPV(shaderBytes, uniformProperties, uniformPropertyMembers);
                    operation.CreateOrSetArray(uniformProperties.AsSpan());
                    operation.CreateOrSetArray(uniformPropertyMembers.AsSpan());
                    if (request.type == ShaderType.Vertex)
                    {
                        using List<ShaderPushConstant> pushConstants = new();
                        using List<ShaderVertexInputAttribute> vertexInputAttributes = new();
                        shaderCompiler.ReadPushConstantsFromSPV(shaderBytes, pushConstants);
                        shaderCompiler.ReadVertexInputAttributesFromSPV(shaderBytes, vertexInputAttributes);
                        operation.CreateOrSetArray(pushConstants.AsSpan());
                        operation.CreateOrSetArray(vertexInputAttributes.AsSpan());
                    }

                    if (request.type == ShaderType.Fragment)
                    {
                        using List<ShaderSamplerProperty> textureProperties = new();
                        shaderCompiler.ReadTexturePropertiesFromSPV(shaderBytes, textureProperties);
                        operation.CreateOrSetArray(textureProperties.AsSpan());
                    }

                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }

        [Conditional("DEBUG")]
        private static void ThrowIfUnknownShaderType(ShaderType type)
        {
            if (type == ShaderType.Unknown || type > ShaderType.Geometry)
            {
                throw new NotSupportedException($"Unknown shader type `{type}`");
            }
        }
    }
}