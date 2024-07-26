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

        public ShaderImportSystem(World world) : base(world)
        {
            shaderQuery = new(world);
            shaderCompiler = new();
            Subscribe<ShaderUpdate>(Update);
        }

        public override void Dispose()
        {
            DisposeShaderUniformProperties();
            shaderCompiler.Dispose();
            shaderQuery.Dispose();
            base.Dispose();
        }

        private void DisposeShaderUniformProperties()
        {
            shaderQuery.Fill();
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
            shaderQuery.Fill();
            foreach (Query<IsShader>.Result result in shaderQuery)
            {
                ref IsShader shader = ref result.Component1;
                if (shader.changed)
                {
                    shader.changed = false;
                    Update(result.entity, shader.vertex, shader.fragment);
                }
            }
        }

        /// <summary>
        /// Updates the shader entity with up to date <see cref="ShaderUniformProperty"/>,
        /// <see cref="ShaderSamplerProperty"/>, and <see cref="ShaderVertexInputAttribute"/> collections.
        /// </summary>
        private void Update(eint shader, eint vertex, eint fragment)
        {
            UnmanagedList<byte> vertexBytes = world.GetList<byte>(vertex);
            UnmanagedList<byte> fragmentBytes = world.GetList<byte>(fragment);
            ReadOnlySpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertexBytes.AsSpan(), ShaderStage.Vertex);
            ReadOnlySpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragmentBytes.AsSpan(), ShaderStage.Fragment);

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

            UnmanagedList<ShaderUniformProperty> uniformProperties = world.GetList<ShaderUniformProperty>(shader);
            UnmanagedList<ShaderSamplerProperty> textureProperties = world.GetList<ShaderSamplerProperty>(shader);
            UnmanagedList<ShaderVertexInputAttribute> vertexInputAttributes = world.GetList<ShaderVertexInputAttribute>(shader);
            for (uint i = 0; i < uniformProperties.Count; i++)
            {
                uniformProperties[i].Dispose();
            }

            uniformProperties.Clear();
            textureProperties.Clear();
            vertexInputAttributes.Clear();

            //populate shader entity with shader property data
            shaderCompiler.ReadUniformPropertiesFromSPV(spvVertex, uniformProperties);
            shaderCompiler.ReadTexturePropertiesFromSPV(spvFragment, textureProperties);
            shaderCompiler.ReadVertexInputAttributesFromSPV(spvVertex, vertexInputAttributes);
        }
    }
}
