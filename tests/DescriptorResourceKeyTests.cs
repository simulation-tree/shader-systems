using System;

namespace Shaders.Tests
{
    public class DescriptorResourceKeyTests
    {
        [Test]
        public void CheckKeyOrder()
        {
            DescriptorResourceKey key = new(4, 9);
            Assert.That(key.Binding, Is.EqualTo(4));
            Assert.That(key.Set, Is.EqualTo(9));
            Assert.That(key.ToString(), Is.EqualTo("4:9"));
        }

        [Test]
        public void ParseFromString()
        {
            DescriptorResourceKey key = DescriptorResourceKey.Parse("4:9".AsSpan());
            Assert.That(key.Binding, Is.EqualTo(4));
            Assert.That(key.Set, Is.EqualTo(9));
        }
    }
}
