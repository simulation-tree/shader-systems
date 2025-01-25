using Data.Systems;
using Shaders.Systems;
using Simulation.Tests;
using Types;
using Worlds;

namespace Shaders.Tests
{
    public class ShaderSystemsTests : SimulationTests
    {
        static ShaderSystemsTests()
        {
            TypeRegistry.Load<Data.Core.TypeBank>();
            TypeRegistry.Load<Shaders.TypeBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            simulator.AddSystem<DataImportSystem>();
            simulator.AddSystem<ShaderImportSystem>();
        }

        protected override Schema CreateSchema()
        {
            Schema schema = base.CreateSchema();
            schema.Load<Data.Core.SchemaBank>();
            schema.Load<Shaders.SchemaBank>();
            return schema;
        }
    }
}