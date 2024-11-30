using Data;
using Data.Components;
using Data.Systems;
using Shaders.Components;
using Shaders.Systems;
using Simulation.Components;
using Simulation.Tests;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Worlds;

namespace Shaders.Tests
{
    public class ShaderTests : SimulationTests
    {
        protected override void SetUp()
        {
            base.SetUp();
            ComponentType.Register<IsShader>();
            ComponentType.Register<IsShaderRequest>();
            ComponentType.Register<IsDataRequest>();
            ComponentType.Register<IsDataSource>();
            ComponentType.Register<IsData>();
            ComponentType.Register<IsProgram>();
            ComponentType.Register<ProgramAllocation>();
            ArrayType.Register<BinaryData>();
            ArrayType.Register<ShaderPushConstant>();
            ArrayType.Register<ShaderSamplerProperty>();
            ArrayType.Register<ShaderUniformProperty>();
            ArrayType.Register<ShaderUniformPropertyMember>();
            ArrayType.Register<ShaderVertexInputAttribute>();
            Simulator.AddSystem<DataImportSystem>();
            Simulator.AddSystem<ShaderImportSystem>();
        }

        [Test, CancelAfter(4000)]
        public async Task CompileGLSLToSPV(CancellationToken cancellation)
        {
            string fragmentSource =
                @"#version 450

                layout(location = 0) in vec4 fragColor;
                layout(location = 1) in vec2 uv;
                layout(binding = 3) uniform sampler2D mainTexture;
                layout(location = 0) out vec4 outColor;

                void main() {
                    vec4 texel = texture(mainTexture, uv);
                    outColor = fragColor * texel.a;
                }";

            string vertexSource =
                @"#version 450

                layout(location = 0) in vec3 inPosition;
                layout(location = 1) in vec2 inUv;

                layout(push_constant) uniform EntityData {
                    vec4 color;
	                mat4 model;
                } entity;

                layout(binding = 2) uniform CameraInfo {
	                mat4 proj;
                    mat4 view;
                } cameraInfo;

                layout(location = 0) out vec4 fragColor;
                layout(location = 1) out vec2 uv;

                out gl_PerVertex 
                {
                    vec4 gl_Position;   
                };

                void main() {
                    gl_Position = cameraInfo.proj * cameraInfo.view * entity.model * vec4(inPosition, 1.0);
                    fragColor = entity.color;
                    uv = inUv;
                }";

            DataSource vertexFile = new(World, "vertex.glsl");
            vertexFile.Write(vertexSource);

            DataSource fragmentFile = new(World, "fragment.glsl");
            fragmentFile.Write(fragmentSource);

            Shader shader = new(World, "vertex.glsl", "fragment.glsl");

            await shader.UntilCompliant(Simulate, cancellation);

            Assert.That(shader.VertexAttributes.Length, Is.EqualTo(2));
            ShaderVertexInputAttribute first = shader.VertexAttributes[0];
            Assert.That(first.name.ToString(), Is.EqualTo("inPosition"));
            Assert.That(first.location, Is.EqualTo(0));
            Assert.That(first.binding, Is.EqualTo(0));
            Assert.That(first.offset, Is.EqualTo(0));
            Assert.That(first.Type, Is.EqualTo(typeof(Vector3)));

            ShaderVertexInputAttribute second = shader.VertexAttributes[1];
            Assert.That(second.name.ToString(), Is.EqualTo("inUv"));
            Assert.That(second.location, Is.EqualTo(1));
            Assert.That(second.binding, Is.EqualTo(0));
            Assert.That(second.offset, Is.EqualTo(12));
            Assert.That(second.Type, Is.EqualTo(typeof(Vector2)));

            Assert.That(shader.UniformProperties.Length, Is.EqualTo(1));
            ShaderUniformProperty cameraInfo = shader.UniformProperties[0];
            Assert.That(cameraInfo.label.ToString(), Is.EqualTo("cameraInfo"));
            Assert.That(cameraInfo.binding, Is.EqualTo(2));
            Assert.That(cameraInfo.set, Is.EqualTo(0));
            Assert.That(shader.GetMemberCount("cameraInfo"), Is.EqualTo(2));

            ShaderUniformPropertyMember member = shader.GetMember("cameraInfo", 0);
            Assert.That(member.name.ToString(), Is.EqualTo("proj"));
            member = shader.GetMember("cameraInfo", 1);
            Assert.That(member.name.ToString(), Is.EqualTo("view"));

            Assert.That(shader.SamplerProperties.Length, Is.EqualTo(1));
            ShaderSamplerProperty texture = shader.SamplerProperties[0];
            Assert.That(texture.binding, Is.EqualTo(3));
            Assert.That(texture.set, Is.EqualTo(0));
            Assert.That(texture.name.ToString(), Is.EqualTo("mainTexture"));

            Assert.That(shader.PushConstants.Length, Is.EqualTo(2));
            ShaderPushConstant entityColor = shader.PushConstants[0];
            Assert.That(entityColor.propertyName.ToString(), Is.EqualTo("entity"));
            Assert.That(entityColor.memberName.ToString(), Is.EqualTo("color"));
            Assert.That(entityColor.size, Is.EqualTo(16));
            Assert.That(entityColor.offset, Is.EqualTo(0));

            ShaderPushConstant entityModel = shader.PushConstants[1];
            Assert.That(entityModel.propertyName.ToString(), Is.EqualTo("entity"));
            Assert.That(entityModel.memberName.ToString(), Is.EqualTo("model"));
            Assert.That(entityModel.size, Is.EqualTo(64));
            Assert.That(entityModel.offset, Is.EqualTo(16));

            //manually disposed instead of `using`, otherwise the teardown
            //will throw too early
        }
    }
}
