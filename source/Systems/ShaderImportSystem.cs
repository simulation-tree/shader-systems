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
        private readonly Dictionary<Entity, uint> shaderVersions;
        private readonly Stack<Operation> operations;

        public ShaderImportSystem()
        {
            shaderCompiler = new();
            shaderVersions = new(4);
            operations = new();
        }

        public void Dispose()
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Dispose();
            }

            operations.Dispose();
            shaderCompiler.Dispose();
            shaderVersions.Dispose();
        }

        void ISystem.Update(Simulator simulator, double deltaTime)
        {
            World world = simulator.world;
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
                            if (TryLoadShader(shader, request, simulator))
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

            PerformOperations(world);
        }

        private void PerformOperations(World world)
        {
            while (operations.TryPop(out Operation operation))
            {
                operation.Perform(world);
                operation.Dispose();
            }
        }

        private bool TryLoadShader(Entity shader, IsShaderRequest request, Simulator simulator)
        {
            ThrowIfUnknownShaderType(request.type);

            LoadData message = new(shader.world, request.address);
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

                Operation operation = new();
                operation.SelectEntity(shader);
                shader.TryGetComponent(out IsShader component);
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

                operations.Push(operation);
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