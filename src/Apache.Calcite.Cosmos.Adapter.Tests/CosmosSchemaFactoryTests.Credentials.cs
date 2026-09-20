using System;

using Azure.Identity;

using FluentAssertions;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    public partial class CosmosSchemaFactoryTests
    {

        /// <summary>
        /// How a model says who it is.
        /// </summary>
        /// <remarks>
        /// A client is built without talking to anything — the SDK connects on first use — so what a set of
        /// operands resolves to can be checked here rather than against an account.
        /// </remarks>
        public class Credentials
        {

            const string Endpoint = "https://example.documents.azure.com:443/";

            // A syntactically valid key. Not a secret, and not an account: base64 of 64 bytes, which is
            // what the SDK validates before it decides whether it can reach anything.
            const string Key = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

            static java.util.Map Operand(params (string Key, string Value)[] entries)
            {
                var operand = new java.util.HashMap();
                foreach (var (key, value) in entries)
                    operand.put(key, value);

                return operand;
            }

            [Fact]
            public void AKeyIsUsedWhereOneIsGiven()
            {
                using var client = CosmosSchemaFactory.CreateClient(Operand((CosmosSchemaFactory.EndpointOperand, Endpoint), (CosmosSchemaFactory.KeyOperand, Key)));

                client.Endpoint.Should().Be(new Uri(Endpoint));
            }

            /// <summary>
            /// An endpoint on its own means Entra, rather than meaning a missing operand.
            /// </summary>
            /// <remarks>
            /// The absence is the request. There is no other sensible reading of "no key and no factory",
            /// and requiring a second operand to say so would only be a way to get it wrong.
            /// </remarks>
            [Fact]
            public void AnEndpointWithoutAKeyAuthenticatesAsTheAmbientIdentity()
            {
                using var client = CosmosSchemaFactory.CreateClient(Operand((CosmosSchemaFactory.EndpointOperand, Endpoint)));

                client.Endpoint.Should().Be(new Uri(Endpoint));
            }

            [Fact]
            public void AnEmptyKeyIsTreatedAsNoKey()
            {
                using var client = CosmosSchemaFactory.CreateClient(Operand((CosmosSchemaFactory.EndpointOperand, Endpoint), (CosmosSchemaFactory.KeyOperand, "")));

                client.Endpoint.Should().Be(new Uri(Endpoint));
            }

            [Fact]
            public void AnEndpointIsStillRequired()
            {
                var create = () => CosmosSchemaFactory.CreateClient(Operand((CosmosSchemaFactory.KeyOperand, Key)));

                create.Should().Throw<ArgumentException>()
                    .WithMessage($"*{CosmosSchemaFactory.EndpointOperand}*");
            }

            [Fact]
            public void TheCredentialIsBuiltFromTheAmbientIdentity()
            {
                CosmosSchemaFactory.CreateCredential(Operand()).Should().BeOfType<DefaultAzureCredential>();
            }

            /// <remarks>
            /// Both narrow an otherwise ambiguous identity: a tenant where the signed-in one is not the
            /// right one, and a client id where more than one managed identity is assigned. Neither is a
            /// credential in itself, which is why they are accepted rather than required.
            /// </remarks>
            [Fact]
            public void TenantAndClientNarrowTheIdentityWithoutBeingRequired()
            {
                var credential = CosmosSchemaFactory.CreateCredential(Operand(
                    (CosmosSchemaFactory.TenantIdOperand, "00000000-0000-0000-0000-000000000000"),
                    (CosmosSchemaFactory.ClientIdOperand, "11111111-1111-1111-1111-111111111111")));

                credential.Should().BeOfType<DefaultAzureCredential>();
            }

        }

    }

}
