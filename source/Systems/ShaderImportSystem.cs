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
        private readonly ConcurrentQueue<UnmanagedArray<Instruction>> operations;

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
            while (operations.TryDequeue(out UnmanagedArray<Instruction> operation))
            {
                foreach (Instruction instruction in operation)
                {
                    instruction.Dispose();
                }

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
                    shaderVersions.Add(shaderEntity, default);
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = shaderVersions[shaderEntity] != request.version;
                }

                if (sourceChanged)
                {
                    shaderVersions[shaderEntity] = request.version;
                    //ThreadPool.QueueUserWorkItem(ImportShaderDataOntoEntity, (shaderEntity, request), false);
                    ImportShaderDataOntoEntity((shaderEntity, request));
                }
            }
        }

        private void PerformOperations()
        {
            while (operations.TryDequeue(out UnmanagedArray<Instruction> operation))
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
        private void ImportShaderDataOntoEntity((eint shader, IsShaderRequest request) input)
        {
            eint shader = input.shader;
            IsShaderRequest request = input.request;
            DataRequest vertex = new(world, world.GetReference(shader, request.vertex));
            DataRequest fragment = new(world, world.GetReference(shader, request.fragment));
            Console.WriteLine($"Waiting for shader request `{shader}` to have data available");
            while (!vertex.IsLoaded || !fragment.IsLoaded)
            {
                Thread.Sleep(1);
            }

            Console.WriteLine($"Starting shader compilation for `{shader}`");
            ReadOnlySpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertex.Data, ShaderStage.Vertex);
            ReadOnlySpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragment.Data, ShaderStage.Fragment);

            Span<Instruction> instructions = stackalloc Instruction[20];
            int instructionCount = 0;
            if (world.TryGetComponent(shader, out IsShader component))
            {
                eint existingVertex = world.GetReference(shader, component.vertex);
                eint existingFragment = world.GetReference(shader, component.fragment);

                instructions[instructionCount++] = Instruction.SelectEntity(existingVertex);
                instructions[instructionCount++] = Instruction.ClearList<byte>();
                instructions[instructionCount++] = Instruction.AddElements(spvVertex);

                instructions[instructionCount++] = Instruction.SelectEntity(existingFragment);
                instructions[instructionCount++] = Instruction.ClearList<byte>();
                instructions[instructionCount++] = Instruction.AddElements(spvFragment);

                component.version++;
                instructions[instructionCount++] = Instruction.SelectEntity(shader);
                instructions[instructionCount++] = Instruction.SetComponent(component);
            }
            else
            {
                instructions[instructionCount++] = Instruction.CreateEntity();
                instructions[instructionCount++] = Instruction.CreateList<byte>();
                instructions[instructionCount++] = Instruction.AddElements(spvVertex);

                instructions[instructionCount++] = Instruction.CreateEntity();
                instructions[instructionCount++] = Instruction.CreateList<byte>();
                instructions[instructionCount++] = Instruction.AddElements(spvFragment);

                instructions[instructionCount++] = Instruction.SelectEntity(shader);
                instructions[instructionCount++] = Instruction.AddReference(1); //for vertex
                instructions[instructionCount++] = Instruction.AddReference(0); //for fragment

                instructions[instructionCount++] = Instruction.AddComponent(new IsShader((rint)1, (rint)2));
            }

            //make sure lists for shader properties exists
            if (!world.ContainsList<ShaderPushConstant>(shader))
            {
                instructions[instructionCount++] = Instruction.CreateList<ShaderPushConstant>();
            }
            else
            {
                instructions[instructionCount++] = Instruction.ClearList<ShaderPushConstant>();
            }

            if (!world.ContainsList<ShaderUniformProperty>(shader))
            {
                instructions[instructionCount++] = Instruction.CreateList<ShaderUniformProperty>();
            }
            else
            {
                instructions[instructionCount++] = Instruction.ClearList<ShaderUniformProperty>();
            }

            if (!world.ContainsList<ShaderUniformPropertyMember>(shader))
            {
                instructions[instructionCount++] = Instruction.CreateList<ShaderUniformPropertyMember>();
            }
            else
            {
                instructions[instructionCount++] = Instruction.ClearList<ShaderUniformPropertyMember>();
            }

            if (!world.ContainsList<ShaderSamplerProperty>(shader))
            {
                instructions[instructionCount++] = Instruction.CreateList<ShaderSamplerProperty>();
            }
            else
            {
                instructions[instructionCount++] = Instruction.ClearList<ShaderSamplerProperty>();
            }

            if (!world.ContainsList<ShaderVertexInputAttribute>(shader))
            {
                instructions[instructionCount++] = Instruction.CreateList<ShaderVertexInputAttribute>();
            }
            else
            {
                instructions[instructionCount++] = Instruction.ClearList<ShaderVertexInputAttribute>();
            }

            using UnmanagedList<ShaderPushConstant> pushConstants = new();
            using UnmanagedList<ShaderUniformProperty> uniformProperties = new();
            using UnmanagedList<ShaderUniformPropertyMember> uniformPropertyMembers = new();
            using UnmanagedList<ShaderSamplerProperty> textureProperties = new();
            using UnmanagedList<ShaderVertexInputAttribute> vertexInputAttributes = new();

            //populate shader entity with shader property data
            shaderCompiler.ReadPushConstantsFromSPV(spvVertex, pushConstants);
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex, uniformProperties, uniformPropertyMembers);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment, textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex, vertexInputAttributes);

            instructions[instructionCount++] = Instruction.AddElements(pushConstants);
            instructions[instructionCount++] = Instruction.AddElements(uniformProperties);
            instructions[instructionCount++] = Instruction.AddElements(uniformPropertyMembers);
            instructions[instructionCount++] = Instruction.AddElements(textureProperties);
            instructions[instructionCount++] = Instruction.AddElements(vertexInputAttributes);
            operations.Enqueue(new(instructions[..instructionCount]));
            Console.WriteLine($"Shader `{shader}` compiled with vertex `{vertex}` and fragment `{fragment}`");
        }
    }
}
