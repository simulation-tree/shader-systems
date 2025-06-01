using Data;
using Data.Messages;
using Data.Systems;
using Shaders.Systems;
using Simulation.Tests;
using Types;
using Worlds;

namespace Shaders.Tests
{
    public class ShaderSystemsTests : SimulationTests
    {
        public World world;

        static ShaderSystemsTests()
        {
            MetadataRegistry.Load<DataMetadataBank>();
            MetadataRegistry.Load<ShadersMetadataBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            Schema schema = new();
            schema.Load<DataSchemaBank>();
            schema.Load<ShadersSchemaBank>();
            world = new(schema);
            Simulator.Add(new DataImportSystem(Simulator, world));
            Simulator.Add(new ShaderImportSystem(Simulator, world));
        }

        protected override void TearDown()
        {
            Simulator.Remove<ShaderImportSystem>();
            Simulator.Remove<DataImportSystem>();
            world.Dispose();
            base.TearDown();
        }

        protected override void Update(double deltaTime)
        {
            Simulator.Broadcast(new DataUpdate(deltaTime));
        }
    }
}