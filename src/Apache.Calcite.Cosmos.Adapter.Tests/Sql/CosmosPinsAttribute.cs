using System;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Marks a measurement as the one that asserts a registry row's outcomes, by the row's id: a row's <c>PinnedBy</c>
    /// names its test, and this is the test naming its row back, so a renamed row or a moved assertion is caught.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class CosmosPinsAttribute(string id) : Attribute
    {

        public string Id { get; } = id;

    }

}
