using Data;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Types;

namespace Shaders.Tests
{
    public class ImportTests : ShaderSystemsTests
    {
        [Test, CancelAfter(4000)]
        public async Task CompileGLSLToSPV(CancellationToken cancellation)
        {
            const string FragmentSource =
                @"#version 450

                layout(location = 0) in vec4 fragColor;
                layout(location = 1) in vec2 uv;
                layout(binding = 3) uniform sampler2D mainTexture;
                layout(location = 0) out vec4 outColor;

                void main() {
                    vec4 texel = texture(mainTexture, uv);
                    outColor = fragColor * texel.a;
                }";

            const string VertexSource =
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

            DataSource vertexFile = new(world, "vertex.glsl");
            vertexFile.WriteUTF8(VertexSource);

            DataSource fragmentFile = new(world, "fragment.glsl");
            fragmentFile.WriteUTF8(FragmentSource);

            Shader vertexShader = new(world, "vertex.glsl", ShaderType.Vertex);
            Shader fragmentShader = new(world, "fragment.glsl", ShaderType.Fragment);

            await vertexShader.UntilCompliant(Simulate, cancellation);
            await fragmentShader.UntilCompliant(Simulate, cancellation);

            Assert.That(vertexShader.VertexInputAttributes.Length, Is.EqualTo(2));
            ShaderVertexInputAttribute first = vertexShader.VertexInputAttributes[0];
            Assert.That(first.name.ToString(), Is.EqualTo("inPosition"));
            Assert.That(first.location, Is.EqualTo(0));
            Assert.That(first.binding, Is.EqualTo(0));
            Assert.That(first.offset, Is.EqualTo(0));
            Assert.That(first.Type, Is.EqualTo(TypeRegistry.GetType<Vector3>()));

            ShaderVertexInputAttribute second = vertexShader.VertexInputAttributes[1];
            Assert.That(second.name.ToString(), Is.EqualTo("inUv"));
            Assert.That(second.location, Is.EqualTo(1));
            Assert.That(second.binding, Is.EqualTo(0));
            Assert.That(second.offset, Is.EqualTo(12));
            Assert.That(second.Type, Is.EqualTo(TypeRegistry.GetType<Vector2>()));

            Assert.That(vertexShader.UniformProperties.Length, Is.EqualTo(1));
            ShaderUniformProperty cameraInfo = vertexShader.UniformProperties[0];
            Assert.That(cameraInfo.label.ToString(), Is.EqualTo("cameraInfo"));
            Assert.That(cameraInfo.binding, Is.EqualTo(2));
            Assert.That(cameraInfo.set, Is.EqualTo(0));
            Assert.That(vertexShader.GetMemberCount("cameraInfo"), Is.EqualTo(2));

            ShaderUniformPropertyMember member = vertexShader.GetMember("cameraInfo", 0);
            Assert.That(member.name.ToString(), Is.EqualTo("proj"));
            member = vertexShader.GetMember("cameraInfo", 1);
            Assert.That(member.name.ToString(), Is.EqualTo("view"));

            Assert.That(fragmentShader.SamplerProperties.Length, Is.EqualTo(1));
            ShaderSamplerProperty texture = fragmentShader.SamplerProperties[0];
            Assert.That(texture.binding, Is.EqualTo(3));
            Assert.That(texture.set, Is.EqualTo(0));
            Assert.That(texture.name.ToString(), Is.EqualTo("mainTexture"));

            Assert.That(vertexShader.PushConstants.Length, Is.EqualTo(2));
            ShaderPushConstant entityColor = vertexShader.PushConstants[0];
            Assert.That(entityColor.propertyName.ToString(), Is.EqualTo("entity"));
            Assert.That(entityColor.memberName.ToString(), Is.EqualTo("color"));
            Assert.That(entityColor.size, Is.EqualTo(16));
            Assert.That(entityColor.offset, Is.EqualTo(0));

            ShaderPushConstant entityModel = vertexShader.PushConstants[1];
            Assert.That(entityModel.propertyName.ToString(), Is.EqualTo("entity"));
            Assert.That(entityModel.memberName.ToString(), Is.EqualTo("model"));
            Assert.That(entityModel.size, Is.EqualTo(64));
            Assert.That(entityModel.offset, Is.EqualTo(16));

            //manually disposed instead of `using`, otherwise the teardown
            //will throw too early
        }

        [Test, CancelAfter(4000)]
        public async Task ImportInstancedShader(CancellationToken cancellation)
        {
            const string VertexSource =
                @"#version 450 core
                layout (location = 0) in vec2 aPos;
                layout (location = 1) in vec3 aColor;

                layout (binding = 2) readonly buffer InstanceData {
                    vec2 offsets[];
                } instanceData;

                layout(location = 0) out vec3 fColor;
                
                out gl_PerVertex 
                {
                    vec4 gl_Position;   
                };

                void main()
                {
                    vec2 offset = instanceData.offsets[int(gl_InstanceIndex)];
                    gl_Position = vec4(aPos + offset, 0.0, 1.0);
                    fColor = aColor;
                }";

            DataSource vertexFile = new(world, "vertex.glsl");
            vertexFile.WriteUTF8(VertexSource);

            Shader vertexShader = new(world, "vertex.glsl", ShaderType.Vertex);

            await vertexShader.UntilCompliant(Simulate, cancellation);

            Assert.That(vertexShader.IsInstanced, Is.True);

            Assert.That(vertexShader.VertexInputAttributes.Length, Is.EqualTo(2));
            ShaderVertexInputAttribute first = vertexShader.VertexInputAttributes[0];
            Assert.That(first.name.ToString(), Is.EqualTo("aPos"));
            Assert.That(first.location, Is.EqualTo(0));
            Assert.That(first.binding, Is.EqualTo(0));
            Assert.That(first.offset, Is.EqualTo(0));
            Assert.That(first.Type, Is.EqualTo(TypeRegistry.GetType<Vector2>()));

            ShaderVertexInputAttribute second = vertexShader.VertexInputAttributes[1];
            Assert.That(second.name.ToString(), Is.EqualTo("aColor"));
            Assert.That(second.location, Is.EqualTo(1));
            Assert.That(second.binding, Is.EqualTo(0));
            Assert.That(second.offset, Is.EqualTo(8));
            Assert.That(second.Type, Is.EqualTo(TypeRegistry.GetType<Vector3>()));
        }
    }
}
