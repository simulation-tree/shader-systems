using Data;
using Shaders.Components;
using Shaders.Events;
using Simulation;
using System;
using System.Collections.Concurrent;
using System.Threading;
using Unmanaged.Collections;

namespace Shaders.Systems
{
    public class ShaderImportSystem : SystemBase
    {
        private readonly Query<IsShaderRequest> requestsQuery;
        private readonly Query<IsShader> shaderQuery;
        private readonly ShaderCompiler shaderCompiler;
        private readonly UnmanagedDictionary<eint, uint> shaderVersions;
        private readonly ConcurrentQueue<Operation> operations;

        public ShaderImportSystem(World world) : base(world)
        {
            requestsQuery = new(world);
            shaderQuery = new(world);
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
            requestsQuery.Update();
            foreach (var r in requestsQuery)
            {
                IsShaderRequest request = r.Component1;
                bool sourceChanged = false;
                eint shaderEntity = r.entity;
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
                        shaderVersions[shaderEntity] = request.version;
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
        private bool TryImportShaderDataOntoEntity((eint shader, IsShaderRequest request) input)
        {
            eint shader = input.shader;
            IsShaderRequest request = input.request;
            DataRequest vertex = new(world, world.GetReference(shader, request.vertex));
            DataRequest fragment = new(world, world.GetReference(shader, request.fragment));
            while (!vertex.IsLoaded || !fragment.IsLoaded)
            {
                Console.WriteLine($"Waiting for shader request `{shader}` to have data available");
                //todo: fault: if data update performs after shader update, then this may never break, kinda scary
                //Console.WriteLine("hanging shaders");
                //Thread.Sleep(1);
                return false;
            }

            Console.WriteLine($"Starting shader compilation for `{shader}`");
            ReadOnlySpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertex.Data, ShaderStage.Vertex);
            ReadOnlySpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragment.Data, ShaderStage.Fragment);

            Operation operation = new();
            if (world.TryGetComponent(shader, out IsShader component))
            {
                eint existingVertex = world.GetReference(shader, component.vertex);
                eint existingFragment = world.GetReference(shader, component.fragment);

                operation.SelectEntity(existingVertex);
                operation.ClearList<byte>();
                operation.AppendToList(spvVertex);
                operation.ClearSelection();

                operation.SelectEntity(existingFragment);
                operation.ClearList<byte>();
                operation.AppendToList(spvFragment);
                operation.ClearSelection();

                component.version++;
                operation.SelectEntity(shader);
                operation.SetComponent(component);
            }
            else
            {
                operation.CreateEntity();
                operation.CreateList<byte>();
                operation.AppendToList(spvVertex);
                operation.ClearSelection();

                operation.CreateEntity();
                operation.CreateList<byte>();
                operation.AppendToList(spvFragment);
                operation.ClearSelection();

                operation.SelectEntity(shader);
                operation.AddReference(1); //for vertex
                operation.AddReference(0); //for fragment

                uint referenceCount = world.GetReferenceCount(shader);
                operation.AddComponent(new IsShader((rint)(referenceCount + 1), (rint)(referenceCount + 2)));
            }

            //make sure lists for shader properties exists
            if (!world.ContainsList<ShaderPushConstant>(shader))
            {
                operation.CreateList<ShaderPushConstant>();
            }
            else
            {
                operation.ClearList<ShaderPushConstant>();
            }

            if (!world.ContainsList<ShaderUniformProperty>(shader))
            {
                operation.CreateList<ShaderUniformProperty>();
            }
            else
            {
                operation.ClearList<ShaderUniformProperty>();
            }

            if (!world.ContainsList<ShaderUniformPropertyMember>(shader))
            {
                operation.CreateList<ShaderUniformPropertyMember>();
            }
            else
            {
                operation.ClearList<ShaderUniformPropertyMember>();
            }

            if (!world.ContainsList<ShaderSamplerProperty>(shader))
            {
                operation.CreateList<ShaderSamplerProperty>();
            }
            else
            {
                operation.ClearList<ShaderSamplerProperty>();
            }

            if (!world.ContainsList<ShaderVertexInputAttribute>(shader))
            {
                operation.CreateList<ShaderVertexInputAttribute>();
            }
            else
            {
                operation.ClearList<ShaderVertexInputAttribute>();
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

            operation.AppendToList(pushConstants);
            operation.AppendToList(uniformProperties);
            operation.AppendToList(uniformPropertyMembers);
            operation.AppendToList(textureProperties);
            operation.AppendToList(vertexInputAttributes);
            operations.Enqueue(operation);
            Console.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
            return true;
        }
    }
}
