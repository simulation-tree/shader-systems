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
                EntityID shader = result.entity;
                if (world.ContainsCollection<ShaderUniformProperty>(shader))
                {
                    UnmanagedList<ShaderUniformProperty> uniformProperties = world.GetCollection<ShaderUniformProperty>(shader);
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
        private void Update(EntityID shader, EntityID vertex, EntityID fragment)
        {
            UnmanagedList<byte> vertexBytes = world.GetCollection<byte>(vertex);
            UnmanagedList<byte> fragmentBytes = world.GetCollection<byte>(fragment);
            ReadOnlySpan<byte> spvVertex = shaderCompiler.GLSLToSPV(vertexBytes.AsSpan(), ShaderStage.Vertex);
            ReadOnlySpan<byte> spvFragment = shaderCompiler.GLSLToSPV(fragmentBytes.AsSpan(), ShaderStage.Fragment);

            if (!world.ContainsCollection<ShaderUniformProperty>(shader))
            {
                world.CreateCollection<ShaderUniformProperty>(shader);
            }

            if (!world.ContainsCollection<ShaderSamplerProperty>(shader))
            {
                world.CreateCollection<ShaderSamplerProperty>(shader);
            }

            if (!world.ContainsCollection<ShaderVertexInputAttribute>(shader))
            {
                world.CreateCollection<ShaderVertexInputAttribute>(shader);
            }

            UnmanagedList<ShaderUniformProperty> uniformProperties = world.GetCollection<ShaderUniformProperty>(shader);
            UnmanagedList<ShaderSamplerProperty> textureProperties = world.GetCollection<ShaderSamplerProperty>(shader);
            UnmanagedList<ShaderVertexInputAttribute> vertexInputAttributes = world.GetCollection<ShaderVertexInputAttribute>(shader);
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
