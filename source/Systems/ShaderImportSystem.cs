using Shaders.Components;
using Shaders.Events;
using Simulation;
using System;
using Unmanaged.Collections;

namespace Shaders.Systems
{
    public class ShaderImportSystem : SystemBase
    {
        private readonly Query<IsShader> shaderQuery;
        private readonly ShaderCompiler shaderCompiler;
        private readonly UnmanagedDictionary<eint, uint> shaderVersions;

        public ShaderImportSystem(World world) : base(world)
        {
            shaderQuery = new(world);
            shaderCompiler = new();
            shaderVersions = new();
            Subscribe<ShaderUpdate>(Update);
        }

        public override void Dispose()
        {
            DisposeShaderUniformProperties();
            shaderCompiler.Dispose();
            shaderQuery.Dispose();
            shaderVersions.Dispose();
            base.Dispose();
        }

        private void DisposeShaderUniformProperties()
        {
            shaderQuery.Update();
            foreach (Query<IsShader>.Result result in shaderQuery)
            {
                eint shader = result.entity;
                if (world.ContainsList<ShaderUniformProperty>(shader))
                {
                    UnmanagedList<ShaderUniformProperty> uniformProperties = world.GetList<ShaderUniformProperty>(shader);
                    for (uint i = 0; i < uniformProperties.Count; i++)
                    {
                        uniformProperties[i].Dispose();
                    }
                }
            }
        }

        private void Update(ShaderUpdate e)
        {
            ImportShaders();
        }

        private void ImportShaders()
        {
            shaderQuery.Update();
            foreach (var r in shaderQuery)
            {
                ref IsShader shader = ref r.Component1;
                bool sourceChanged = false;
                if (!shaderVersions.ContainsKey(r.entity))
                {
                    shaderVersions.Add(r.entity, default);
                    sourceChanged = true;
                }
                else
                {
                    sourceChanged = shaderVersions[r.entity] != shader.version;
                }

                if (sourceChanged)
                {
                    Update(r.entity, shader.vertex, shader.fragment);
                    shader = new(shader.version + 1, shader.vertex, shader.fragment);
                    shaderVersions[r.entity] = shader.version;
                    Console.WriteLine($"Shader `{r.entity}` compiled with vertex `{shader.vertex}` and fragment `{shader.fragment}`");
                }
            }
        }

        /// <summary>
        /// Updates the shader entity with up to date <see cref="ShaderUniformProperty"/>,
        /// <see cref="ShaderSamplerProperty"/>, and <see cref="ShaderVertexInputAttribute"/> collections.
        /// <para>Modifies the `byte` lists to contain SPV bytecode.</para>
        /// </summary>
        private void Update(eint shader, eint vertex, eint fragment)
        {
            UnmanagedList<byte> vertexBytes = world.GetList<byte>(vertex);
            UnmanagedList<byte> fragmentBytes = world.GetList<byte>(fragment);
            ReadOnlySpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertexBytes.AsSpan(), ShaderStage.Vertex);
            ReadOnlySpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragmentBytes.AsSpan(), ShaderStage.Fragment);
            vertexBytes.Clear();
            fragmentBytes.Clear();
            vertexBytes.AddRange(spvVertex);
            fragmentBytes.AddRange(spvFragment);

            if (!world.ContainsList<ShaderPushConstant>(shader))
            {
                world.CreateList<ShaderPushConstant>(shader);
            }

            if (!world.ContainsList<ShaderUniformProperty>(shader))
            {
                world.CreateList<ShaderUniformProperty>(shader);
            }

            if (!world.ContainsList<ShaderSamplerProperty>(shader))
            {
                world.CreateList<ShaderSamplerProperty>(shader);
            }

            if (!world.ContainsList<ShaderVertexInputAttribute>(shader))
            {
                world.CreateList<ShaderVertexInputAttribute>(shader);
            }

            UnmanagedList<ShaderPushConstant> pushConstants = world.GetList<ShaderPushConstant>(shader);
            UnmanagedList<ShaderUniformProperty> uniformProperties = world.GetList<ShaderUniformProperty>(shader);
            UnmanagedList<ShaderSamplerProperty> textureProperties = world.GetList<ShaderSamplerProperty>(shader);
            UnmanagedList<ShaderVertexInputAttribute> vertexInputAttributes = world.GetList<ShaderVertexInputAttribute>(shader);
            for (uint i = 0; i < uniformProperties.Count; i++)
            {
                uniformProperties[i].Dispose();
            }

            pushConstants.Clear();
            uniformProperties.Clear();
            textureProperties.Clear();
            vertexInputAttributes.Clear();

            //populate shader entity with shader property data
            shaderCompiler.ReadPushConstantsFromSPV(spvVertex, pushConstants);
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex, uniformProperties);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment, textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex, vertexInputAttributes);
        }
    }
}
