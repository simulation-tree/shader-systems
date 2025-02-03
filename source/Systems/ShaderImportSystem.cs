using Collections;
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
            ComponentQuery<IsShaderRequest> requestQuery = new(world);
            Simulator simulator = systemContainer.simulator;
            foreach (var r in requestQuery)
            {
                ref IsShaderRequest request = ref r.component1;
                Entity shader = new(world, r.entity);
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
                        world.SetComponent(r.entity, dataRequest.BecomeLoaded());
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

        private readonly bool TryLoadShader(Entity shader, IsShaderRequest request, Simulator simulator)
        {
            HandleDataRequest message = new(shader, request.address);
            if (simulator.TryHandleMessage(ref message))
            {
                if (message.loaded)
                {
                    ShaderType type = request.type;
                    Schema schema = shader.world.Schema;

                    Trace.WriteLine($"Loading shader data onto entity `{shader}`");
                    USpan<byte> sourceBytes = message.Bytes;
                    USpan<byte> shaderBytes = shaderCompiler.GLSLToSPV(sourceBytes, type);
                    Operation operation = new();
                    Operation.SelectedEntity selectedEntity = operation.SelectEntity(shader);
                    if (shader.TryGetComponent(out IsShader component))
                    {
                        selectedEntity.SetComponent(component.IncrementVersion(), schema);
                    }
                    else
                    {
                        selectedEntity.AddComponent(new IsShader(0, type), schema);
                    }

                    //set the shader bytes
                    if (shader.ContainsArray<ShaderByte>())
                    {
                        selectedEntity.ResizeArray<ShaderByte>(shaderBytes.Length, schema);
                        selectedEntity.SetArrayElements(0, shaderBytes.As<ShaderByte>(), schema);
                    }
                    else
                    {
                        selectedEntity.CreateArray(shaderBytes.As<ShaderByte>(), schema);
                    }

                    //fill metadata
                    using List<ShaderUniformPropertyMember> uniformPropertyMembers = new();
                    using List<ShaderUniformProperty> uniformProperties = new();
                    shaderCompiler.ReadUniformPropertiesFromSPV(shaderBytes, uniformProperties, uniformPropertyMembers);

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

                    if (type == ShaderType.Vertex)
                    {
                        using List<ShaderPushConstant> pushConstants = new();
                        using List<ShaderVertexInputAttribute> vertexInputAttributes = new();
                        shaderCompiler.ReadPushConstantsFromSPV(shaderBytes, pushConstants);
                        shaderCompiler.ReadVertexInputAttributesFromSPV(shaderBytes, vertexInputAttributes);

                        if (!shader.ContainsArray<ShaderPushConstant>())
                        {
                            selectedEntity.CreateArray(pushConstants.AsSpan(), schema);
                        }
                        else
                        {
                            selectedEntity.ResizeArray<ShaderPushConstant>(pushConstants.Count, schema);
                            selectedEntity.SetArrayElements(0, pushConstants.AsSpan(), schema);
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
                    }

                    if (type == ShaderType.Fragment)
                    {
                        using List<ShaderSamplerProperty> textureProperties = new();
                        shaderCompiler.ReadTexturePropertiesFromSPV(shaderBytes, textureProperties);

                        if (!shader.ContainsArray<ShaderSamplerProperty>())
                        {
                            selectedEntity.CreateArray(textureProperties.AsSpan(), schema);
                        }
                        else
                        {
                            selectedEntity.ResizeArray<ShaderSamplerProperty>(textureProperties.Count, schema);
                            selectedEntity.SetArrayElements(0, textureProperties.AsSpan(), schema);
                        }
                    }

                    operations.Push(operation);
                    return true;
                }
            }

            return false;
        }
    }
}