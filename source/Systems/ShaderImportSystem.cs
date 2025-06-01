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
    [SkipLocalsInit]
    public partial class ShaderImportSystem : SystemBase, IListener<DataUpdate>
    {
        private readonly World world;
        private readonly ShaderCompiler shaderCompiler;
        private readonly Dictionary<uint, uint> shaderVersions;
        private readonly Operation operation;
        private readonly int requestType;
        private readonly int shaderType;
        private readonly int byteArrayType;

        public ShaderImportSystem(Simulator simulator, World world) : base(simulator)
        {
            this.world = world;
            shaderCompiler = new();
            shaderVersions = new(4);
            operation = new(world);

            Schema schema = world.Schema;
            requestType = schema.GetComponentType<IsShaderRequest>();
            shaderType = schema.GetComponentType<IsShader>();
            byteArrayType = schema.GetArrayType<ShaderByte>();
        }

        public override void Dispose()
        {
            operation.Dispose();
            shaderCompiler.Dispose();
            shaderVersions.Dispose();
        }

        void IListener<DataUpdate>.Receive(ref DataUpdate message)
        {
            ReadOnlySpan<Chunk> chunks = world.Chunks;
            for (int c = 0; c < chunks.Length; c++)
            {
                Chunk chunk = chunks[c];
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
                            if (TryLoadShader(shader, request))
                            {
                                Trace.WriteLine($"Shader `{shader}` has been loaded");
                                request.status = IsShaderRequest.Status.Loaded;
                            }
                            else
                            {
                                request.duration += message.deltaTime;
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

            if (operation.TryPerform())
            {
                operation.Reset();
            }
        }

        private bool TryLoadShader(uint shaderEntity, IsShaderRequest request)
        {
            ThrowIfUnknownShaderType(request.type);

            //todo: should shaders be cached based on address? what if its loaded from file on disk and the file changes?
            LoadData message = new(request.address);
            simulator.Broadcast(ref message);
            if (message.TryConsume(out ByteReader data))
            {
                Trace.WriteLine($"Loading shader data onto entity `{shaderEntity}`");
                Span<byte> bytes = data.GetBytes();
                ShaderFlags flags = default;
                if (IsShaderInstanced(bytes))
                {
                    flags |= ShaderFlags.Instanced;
                }

                Span<byte> shaderBytes = shaderCompiler.GLSLToSPV(bytes, request.type);
                data.Dispose();

                operation.SetSelectedEntity(shaderEntity);
                world.TryGetComponent(shaderEntity, shaderType, out IsShader shader);
                shader.version++;
                shader.flags = flags;
                operation.AddOrSetComponent(shader, shaderType);
                operation.CreateOrSetArray(shaderBytes.As<byte, ShaderByte>(), byteArrayType);

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