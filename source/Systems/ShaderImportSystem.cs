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
        private readonly ShaderCompilerContext shaderCompiler;
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
                Span<byte> spvBytes = shaderCompiler.GLSLToSPV(bytes, request.type);
                data.Dispose();

                operation.SetSelectedEntity(shaderEntity);
                world.TryGetComponent(shaderEntity, shaderType, out IsShader shader);
                shader.version++;
                shader.type = request.type;
                operation.AddOrSetComponent(shader, shaderType);
                operation.CreateOrSetArray(spvBytes.As<byte, ShaderByte>(), byteArrayType);

                //fill metadata
                ShaderCompilerContext.Compiler compiler = shaderCompiler.GetCompiler(spvBytes, Vortice.SpirvCross.Backend.GLSL);
                using List<ShaderUniformPropertyMember> uniformPropertyMembers = new();
                using List<ShaderUniformProperty> uniformProperties = new();
                compiler.ReadUniformProperties(uniformProperties, uniformPropertyMembers);
                operation.CreateOrSetArray(uniformProperties.AsSpan());
                operation.CreateOrSetArray(uniformPropertyMembers.AsSpan());
                if (request.type == ShaderType.Vertex)
                {
                    using List<ShaderPushConstant> pushConstants = new();
                    using List<ShaderVertexInputAttribute> vertexInputAttributes = new();
                    compiler.ReadPushConstants(pushConstants);
                    compiler.ReadVertexInputAttributes(vertexInputAttributes);
                    operation.CreateOrSetArray(pushConstants.AsSpan());
                    operation.CreateOrSetArray(vertexInputAttributes.AsSpan());
                }

                if (request.type == ShaderType.Fragment)
                {
                    using List<ShaderSamplerProperty> textureProperties = new();
                    compiler.ReadTextureProperties(textureProperties);
                    operation.CreateOrSetArray(textureProperties.AsSpan());
                }

                using List<ShaderStorageBuffer> storageBuffers = new();
                compiler.ReadStorageBuffers(storageBuffers);
                operation.CreateOrSetArray(storageBuffers.AsSpan());
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