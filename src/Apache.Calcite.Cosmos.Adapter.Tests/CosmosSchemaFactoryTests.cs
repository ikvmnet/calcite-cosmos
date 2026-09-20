namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// What the factory makes of a model's operands, grouped by the operand each set is about.
    /// </summary>
    /// <remarks>
    /// The groups are separate classes because each wants a different fixture — a credential is read
    /// without touching a service, a container declaration is a nested map a model parser hands over,
    /// and a cache policy is the one part of the policy that arrives as untyped JSON.
    /// </remarks>
    public partial class CosmosSchemaFactoryTests
    {

    }

}
