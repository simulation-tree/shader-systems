using Data;
using Shaders.Components;
using Shaders.Events;
using Simulation;
using System;
using System.Collections.Concurrent;
using Unmanaged;
using Unmanaged.Collections;

namespace Shaders.Systems
{
    public class ShaderImportSystem : SystemBase
    {
        private readonly ComponentQuery<IsShaderRequest> requestsQuery;
        private readonly ComponentQuery<IsShader> shaderQuery;
        private readonly ShaderCompiler shaderCompiler;
        private readonly UnmanagedDictionary<uint, uint> shaderVersions;
        private readonly ConcurrentQueue<Operation> operations;

        public ShaderImportSystem(World world) : base(world)
        {
            requestsQuery = new();
            shaderQuery = new();
            shaderCompiler = new();
            shaderVersions = new();
            operations = new();
            Subscribe<ShaderUpdate>(Update);
        }

        public override void Dispose()
        {
            while (operations.TryDequeue(out Operation operation))
            {
                operation.Dispose();
            }

            shaderCompiler.Dispose();
            shaderQuery.Dispose();
            requestsQuery.Dispose();
            shaderVersions.Dispose();
            base.Dispose();
        }

        private void Update(ShaderUpdate e)
        {
            ImportShaders();
            PerformOperations();
        }

        private void ImportShaders()
        {
            requestsQuery.Update(world);
            foreach (var r in requestsQuery)
            {
                IsShaderRequest request = r.Component1;
                bool sourceChanged = false;
                uint shaderEntity = r.entity;
                if (!shaderVersions.ContainsKey(shaderEntity))
                {
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = shaderVersions[shaderEntity] != request.version;
                }

                if (sourceChanged)
                {
                    //ThreadPool.QueueUserWorkItem(ImportShaderDataOntoEntity, (shaderEntity, request), false);
                    if (TryImportShaderDataOntoEntity((shaderEntity, request)))
                    {
                        shaderVersions.AddOrSet(shaderEntity, request.version);
                    }
                }
            }
        }

        private void PerformOperations()
        {
            while (operations.TryDequeue(out Operation operation))
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
        private bool TryImportShaderDataOntoEntity((uint shader, IsShaderRequest request) input)
        {
            uint shader = input.shader;
            IsShaderRequest request = input.request;
            DataRequest vertex = new(world, world.GetReference(shader, request.vertex));
            DataRequest fragment = new(world, world.GetReference(shader, request.fragment));
            while (!vertex.IsCompliant() || !fragment.IsCompliant())
            {
                Console.WriteLine($"Waiting for shader request `{shader}` to have data available");
                //todo: fault: if data update performs after shader update, then this may never break, kinda scary
                //Console.WriteLine("hanging shaders");
                //Thread.Sleep(1);
                return false;
            }

            Console.WriteLine($"Starting shader compilation for `{shader}`");
            USpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertex.Data, ShaderStage.Vertex);
            USpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragment.Data, ShaderStage.Fragment);

            Operation operation = new();
            if (world.TryGetComponent(shader, out IsShader component))
            {
                uint existingVertex = world.GetReference(shader, component.vertex);
                uint existingFragment = world.GetReference(shader, component.fragment);

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

                uint referenceCount = world.GetReferenceCount(shader);
                operation.AddComponent(new IsShader((rint)(referenceCount + 1), (rint)(referenceCount + 2)));
            }

            using UnmanagedList<ShaderPushConstant> pushConstants = new();
            using UnmanagedList<ShaderUniformProperty> uniformProperties = new();
            using UnmanagedList<ShaderUniformPropertyMember> uniformPropertyMembers = new();
            using UnmanagedList<ShaderSamplerProperty> textureProperties = new();
            using UnmanagedList<ShaderVertexInputAttribute> vertexInputAttributes = new();

            //fill in shader data
            shaderCompiler.ReadPushConstantsFromSPV(spvVertex, pushConstants);
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex, uniformProperties, uniformPropertyMembers);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment, textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex, vertexInputAttributes);

            //make sure lists for shader properties exists
            if (!world.ContainsArray<ShaderPushConstant>(shader))
            {
                operation.CreateArray<ShaderPushConstant>(pushConstants.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderPushConstant>(pushConstants.Count);
                operation.SetArrayElements(0, pushConstants.AsSpan());
            }

            if (!world.ContainsArray<ShaderUniformProperty>(shader))
            {
                operation.CreateArray<ShaderUniformProperty>(uniformProperties.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderUniformProperty>(uniformProperties.Count);
                operation.SetArrayElements(0, uniformProperties.AsSpan());
            }

            if (!world.ContainsArray<ShaderUniformPropertyMember>(shader))
            {
                operation.CreateArray<ShaderUniformPropertyMember>(uniformPropertyMembers.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderUniformPropertyMember>(uniformPropertyMembers.Count);
                operation.SetArrayElements(0, uniformPropertyMembers.AsSpan());
            }

            if (!world.ContainsArray<ShaderSamplerProperty>(shader))
            {
                operation.CreateArray<ShaderSamplerProperty>(textureProperties.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderSamplerProperty>(textureProperties.Count);
                operation.SetArrayElements(0, textureProperties.AsSpan());
            }

            if (!world.ContainsArray<ShaderVertexInputAttribute>(shader))
            {
                operation.CreateArray<ShaderVertexInputAttribute>(vertexInputAttributes.AsSpan());
            }
            else
            {
                operation.ResizeArray<ShaderVertexInputAttribute>(vertexInputAttributes.Count);
                operation.SetArrayElements(0, vertexInputAttributes.AsSpan());
            }

            operations.Enqueue(operation);
            Console.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
            return true;
        }
    }
}
