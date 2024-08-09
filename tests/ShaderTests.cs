using Data.Systems;
using Shaders.Systems;
using Simulation;
using System.Numerics;
using Unmanaged;

namespace Shaders.Tests
{
    public class ShaderTests
    {
        [TearDown]
        public void CleanUp()
        {
            Allocations.ThrowIfAny();
        }

        [Test]
        public void CheckResourceDescriptorKeyOrder()
        {
            DescriptorResourceKey key = new(4, 9);
            Assert.That(key.Binding, Is.EqualTo(4));
            Assert.That(key.Set, Is.EqualTo(9));
            Assert.That(key.ToString(), Is.EqualTo("4:9"));
        }

        [Test]
        public void CompileGLSLToSPV()
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

                layout(binding = 0) uniform SpriteInfo {
                    vec4 color;
                } spriteInfo;

                layout(binding = 1) uniform TransformInfo {
	                mat4 model;
                } transformInfo;

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
                    gl_Position = cameraInfo.proj * cameraInfo.view * transformInfo.model * vec4(inPosition, 1.0);
                    fragColor = spriteInfo.color;
                    uv = inUv;
                }";

            using World world = new();
            using DataImportSystem dataImports = new(world);
            ShaderImportSystem shaderImports = new(world);

            Data.Data vertexFile = new(world, "vertex.glsl");
            vertexFile.Write(vertexSource);

            Data.Data fragmentFile = new(world, "fragment.glsl");
            fragmentFile.Write(fragmentSource);

            using Shader shader = new(world, "vertex.glsl", "fragment.glsl");

            Assert.That(shader.GetVertexAttributes().Length, Is.EqualTo(2));
            var first = shader.GetVertexAttributes()[0];
            Assert.That(first.name.ToString(), Is.EqualTo("inPosition"));
            Assert.That(first.location, Is.EqualTo(0));
            Assert.That(first.binding, Is.EqualTo(0));
            Assert.That(first.offset, Is.EqualTo(0));
            Assert.That(first.type, Is.EqualTo(RuntimeType.Get<Vector3>()));

            var second = shader.GetVertexAttributes()[1];
            Assert.That(second.name.ToString(), Is.EqualTo("inUv"));
            Assert.That(second.location, Is.EqualTo(1));
            Assert.That(second.binding, Is.EqualTo(0));
            Assert.That(second.offset, Is.EqualTo(12));
            Assert.That(second.type, Is.EqualTo(RuntimeType.Get<Vector2>()));

            Assert.That(shader.GetUniformProperties().Length, Is.EqualTo(3));
            var spriteInfo = shader.GetUniformProperties()[0];
            Assert.That(spriteInfo.name.ToString(), Is.EqualTo("spriteInfo"));
            Assert.That(spriteInfo.key.Set, Is.EqualTo(0));
            Assert.That(spriteInfo.key.Binding, Is.EqualTo(0));
            Assert.That(spriteInfo.size, Is.EqualTo(16));
            Assert.That(spriteInfo.Members.Length, Is.EqualTo(1));

            var member = spriteInfo.Members[0];
            Assert.That(member.name.ToString(), Is.EqualTo("color"));
            Assert.That(member.type, Is.EqualTo(RuntimeType.Get<Vector4>()));

            var transformInfo = shader.GetUniformProperties()[1];
            Assert.That(transformInfo.name.ToString(), Is.EqualTo("transformInfo"));
            Assert.That(transformInfo.key.Set, Is.EqualTo(0));
            Assert.That(transformInfo.key.Binding, Is.EqualTo(1));
            Assert.That(transformInfo.Members.Length, Is.EqualTo(1));
            member = transformInfo.Members[0];
            Assert.That(member.name.ToString(), Is.EqualTo("model"));

            var cameraInfo = shader.GetUniformProperties()[2];
            Assert.That(cameraInfo.name.ToString(), Is.EqualTo("cameraInfo"));
            Assert.That(cameraInfo.key.Set, Is.EqualTo(0));
            Assert.That(cameraInfo.key.Binding, Is.EqualTo(2));
            Assert.That(cameraInfo.Members.Length, Is.EqualTo(2));
            member = cameraInfo.Members[0];
            Assert.That(member.name.ToString(), Is.EqualTo("proj"));
            member = cameraInfo.Members[1];
            Assert.That(member.name.ToString(), Is.EqualTo("view"));

            Assert.That(shader.GetSamplerProperties().Length, Is.EqualTo(1));
            var texture = shader.GetSamplerProperties()[0];
            Assert.That(texture.key.Binding, Is.EqualTo(3));
            Assert.That(texture.key.Set, Is.EqualTo(0));
            Assert.That(texture.name.ToString(), Is.EqualTo("mainTexture"));

            //manually disposed instead of `using`, otherwise the teardown
            //will throw too early
            shaderImports.Dispose();
        }
    }
}
