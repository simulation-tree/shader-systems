using Collections.Generic;
using Data.Messages;
using Shaders.Components;
using Simulation;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Worlds;

namespace Shaders.Systems
{
    public readonly partial struct ShaderImportSystem : ISystem
    {
        private readonly ShaderCompiler shaderCompiler;
        private readonly Dictionary<Entity, uint> shaderVersions;
        private readonly Stack<Operation> operations;

        public ShaderImportSystem()
        {
            shaderCompiler = new();
            shaderVersions = new(4);
            operations = new();
        }

        public readonly void Dispose()
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Dispose();
            }

            operations.Dispose();
            shaderCompiler.Dispose();
            shaderVersions.Dispose();
        }

        void ISystem.Start(in SystemContext context, in World world)
        {
        }

        void ISystem.Update(in SystemContext context, in World world, in TimeSpan delta)
        {
            int componentType = world.Schema.GetComponentType<IsShaderRequest>();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.ContainsComponent(componentType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsShaderRequest> components = chunk.GetComponents<IsShaderRequest>(componentType);
                    for (int i = 0; i < entities.Length; i++)
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
                            if (TryLoadShader(shader, dataRequest, context))
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

        void ISystem.Finish(in SystemContext context, in World world)
        {
        }

        private readonly void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private readonly bool TryLoadShader(Entity shader, IsShaderRequest request, SystemContext context)
        {
            ThrowIfUnknownShaderType(request.type);

            LoadData message = new(shader.world, request.address);
            if (context.TryHandleMessage(ref message) != default)
            {
                if (message.TryGetBytes(out ReadOnlySpan<byte> data))
                {
                    Trace.WriteLine($"Loading shader data onto entity `{shader}`");
                    ShaderFlags flags = default;
                    if (IsShaderInstanced(data))
                    {
                        flags |= ShaderFlags.Instanced;
                    }

                    Span<byte> shaderBytes = shaderCompiler.GLSLToSPV(data, request.type);
                    message.Dispose();

                    Operation operation = new();
                    operation.SelectEntity(shader);
                    shader.TryGetComponent(out IsShader component);
                    operation.AddOrSetComponent(component.IncrementVersion(flags));
                    operation.CreateOrSetArray(shaderBytes.As<byte, ShaderByte>());

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

        [SkipLocalsInit]
        private static bool IsShaderInstanced(ReadOnlySpan<byte> bytes)
        {
            const string InstanceIndex = "gl_InstanceIndex";
            const string InstanceID = "gl_InstanceID";

            Span<char> textBuffer = stackalloc char[bytes.Length * 2];
            int readLength = bytes.GetUTF8Characters(0, bytes.Length, textBuffer);
            textBuffer = textBuffer.Slice(0, readLength);

            if (textBuffer.IndexOf(InstanceIndex.AsSpan()) != -1 || textBuffer.IndexOf(InstanceID.AsSpan()) != -1)
            {
                return true;
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