using Collections.Generic;
using Data.Messages;
using Shaders.Components;
using Simulation;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unmanaged;
using Worlds;

namespace Shaders.Systems
{
    public partial class ShaderImportSystem : ISystem, IDisposable
    {
        private readonly ShaderCompiler shaderCompiler;
        private readonly Dictionary<uint, uint> shaderVersions;
        private readonly Operation operation;
        private readonly int requestType;
        private readonly int shaderType;

        public ShaderImportSystem(Simulator simulator)
        {
            shaderCompiler = new();
            shaderVersions = new(4);
            operation = new();

            Schema schema = simulator.world.Schema;
            requestType = schema.GetComponentType<IsShaderRequest>();
            shaderType = schema.GetComponentType<IsShader>();
        }

        public void Dispose()
        {
            operation.Dispose();
            shaderCompiler.Dispose();
            shaderVersions.Dispose();
        }

        void ISystem.Update(Simulator simulator, double deltaTime)
        {
            World world = simulator.world;
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.Definition.ContainsComponent(requestType))
                {
                    ReadOnlySpan<uint> entities = chunk.Entities;
                    ComponentEnumerator<IsShaderRequest> components = chunk.GetComponents<IsShaderRequest>(requestType);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        ref IsShaderRequest request = ref components[i];
                        uint shader = entities[i];
                        if (request.status == IsShaderRequest.Status.Submitted)
                        {
                            request.status = IsShaderRequest.Status.Loading;
                            Trace.WriteLine($"Started searching data for shader `{shader}` with address `{request.address}`");
                        }

                        if (request.status == IsShaderRequest.Status.Loading)
                        {
                            if (TryLoadShader(world, shader, request, simulator))
                            {
                                Trace.WriteLine($"Shader `{shader}` has been loaded");
                                request.status = IsShaderRequest.Status.Loaded;
                            }
                            else
                            {
                                request.duration += deltaTime;
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

            if (operation.Count > 0)
            {
                operation.Perform(world);
                operation.Reset();
            }
        }

        private bool TryLoadShader(World world, uint shader, IsShaderRequest request, Simulator simulator)
        {
            ThrowIfUnknownShaderType(request.type);

            LoadData message = new(world, request.address);
            simulator.Broadcast(ref message);
            if (message.TryConsume(out ByteReader data))
            {
                Trace.WriteLine($"Loading shader data onto entity `{shader}`");
                Span<byte> bytes = data.GetBytes();
                ShaderFlags flags = default;
                if (IsShaderInstanced(bytes))
                {
                    flags |= ShaderFlags.Instanced;
                }

                Span<byte> shaderBytes = shaderCompiler.GLSLToSPV(bytes, request.type);
                data.Dispose();

                operation.SetSelectedEntity(shader);
                world.TryGetComponent(shader, shaderType, out IsShader component);
                component.version++;
                component.flags = flags;
                operation.AddOrSetComponent(component);
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

                return true;
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