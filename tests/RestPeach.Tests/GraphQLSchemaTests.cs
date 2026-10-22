using RestPeach.Core.Http;

namespace RestPeach.Tests;

public class GraphQLSchemaTests
{
    private static string T(string kind, string? name, string? of = null) =>
        of is null ? $"{{\"kind\":\"{kind}\",\"name\":\"{name}\",\"ofType\":null}}" : $"{{\"kind\":\"{kind}\",\"name\":null,\"ofType\":{of}}}";

    private static string F(string name, string type, string args = "") =>
        $"{{\"name\":\"{name}\",\"description\":null,\"isDeprecated\":false,\"args\":[{args}],\"type\":{type}}}";

    private static readonly string Json =
        "{\"data\":{\"__schema\":{\"queryType\":{\"name\":\"Query\"},\"mutationType\":{\"name\":\"Mutation\"},\"subscriptionType\":null,\"types\":["
        + Type("OBJECT", "Query", null,
            F("user", T("OBJECT", "User"), "{\"name\":\"id\",\"description\":null,\"type\":" + T("NON_NULL", null, T("SCALAR", "ID")) + "}"),
            F("users", T("NON_NULL", null, T("LIST", null, T("NON_NULL", null, T("OBJECT", "User"))))),
            F("search", T("LIST", null, T("UNION", "Result")))) + ","
        + Type("OBJECT", "Mutation", null, F("login", T("SCALAR", "String"))) + ","
        + Type("OBJECT", "User", null, F("name", T("SCALAR", "String")), F("friends", T("LIST", null, T("OBJECT", "User")))) + ","
        + Type("UNION", "Result", "[{\"name\":\"User\"}]")
        + "]}}}";

    private static string Type(string kind, string name, string? possible, params string[] fields) =>
        $"{{\"kind\":\"{kind}\",\"name\":\"{name}\",\"description\":null,\"fields\":{(fields.Length > 0 ? "[" + string.Join(",", fields) + "]" : "null")},"
        + $"\"inputFields\":null,\"enumValues\":null,\"possibleTypes\":{possible ?? "null"}}}";


    [Fact]
    public void Parses_types_fields_and_signatures()
    {
        var s = GraphQLSchema.Parse(Json);
        Assert.Equal("Query", s.QueryType);
        Assert.Equal("[User!]!", s.Fields("Query").Single(f => f.Name == "users").Type);
        Assert.Equal("User", s.Fields("Query").Single(f => f.Name == "users").NamedType);
        Assert.Equal("(id: ID!): User", s.Fields("Query").Single(f => f.Name == "user").Signature);
        Assert.Equal(new[] { "User" }, s.Types["Result"].PossibleTypes);
        Assert.Throws<FormatException>(() => GraphQLSchema.Parse("{\"errors\":[{\"message\":\"introspection disabled\"}]}"));
    }

    [Theory]
    [InlineData("|", null)]
    [InlineData("query { |", "Query")]
    [InlineData("{ users { |", "User")]
    [InlineData("query Q($id: ID!) { user(id: $id) { friends { |", "User")]
    [InlineData("mutation { |", "Mutation")]
    [InlineData("{ me: user(id: 1) { |", "User")]
    [InlineData("{ users { name } |", "Query")]
    [InlineData("{ search { ... on User { |", "User")]
    [InlineData("fragment F on User { |", "User")]
    [InlineData("{ user(id: \"{ x\" |", "")]
    [InlineData("# { users {\n{ |", "Query")]
    public void Knows_the_type_at_the_cursor(string text, string? expected)
    {
        var s = GraphQLSchema.Parse(Json);
        var caret = text.IndexOf('|');
        Assert.Equal(expected, s.TypeAt(text.Remove(caret, 1), caret));
    }
}
