using Data;
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
            TypeRegistry.Load<DataTypeBank>();
            TypeRegistry.Load<ShadersTypeBank>();
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
            schema.Load<DataSchemaBank>();
            schema.Load<ShadersSchemaBank>();
            return schema;
        }
    }
}