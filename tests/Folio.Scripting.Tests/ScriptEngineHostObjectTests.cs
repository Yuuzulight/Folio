using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class ScriptEngineHostObjectTests
{
    private sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public List<string> Friends { get; } = new();

        public string Greet(string title) => $"Hello, {title} {Name}!";
    }

    [Fact]
    public void HostObjectExposesTemplatePropertiesAndMethods()
    {
        using var engine = new JintScriptEngine();

        var template = new HostObjectTemplate("Person")
            .DefineProperty("name",
                (target, thisVal) => ScriptValue.FromString(((Person)target).Name),
                (target, thisVal, val) => ((Person)target).Name = val.AsString() ?? "")
            .DefineProperty("age",
                (target, thisVal) => ScriptValue.FromNumber(((Person)target).Age),
                (target, thisVal, val) => ((Person)target).Age = (int)val.AsNumber())
            .DefineMethod("greet", (target, thisVal, args) =>
            {
                var title = args.Length > 0 ? args[0].AsString() ?? "" : "";
                var greeting = ((Person)target).Greet(title);
                return ScriptValue.FromString(greeting);
            });

        var person = new Person { Name = "Alice", Age = 30 };
        var personObj = engine.CreateHostObject(template, person);
        engine.SetGlobalProperty("alice", personObj);

        // Read properties
        Assert.Equal("Alice", engine.Evaluate("alice.name").AsString());
        Assert.Equal(30, engine.Evaluate("alice.age").AsNumber());

        // Modify properties from script
        engine.Evaluate("alice.name = 'Bob'; alice.age = 35;");
        Assert.Equal("Bob", person.Name);
        Assert.Equal(35, person.Age);

        // Call method
        var greeting = engine.Evaluate("alice.greet('Dr.')").AsString();
        Assert.Equal("Hello, Dr. Bob!", greeting);
    }

    [Fact]
    public void HostObjectPrototypeInheritance()
    {
        using var engine = new JintScriptEngine();

        var nodeTemplate = new HostObjectTemplate("Node")
            .DefineProperty("nodeType", (target, thisVal) => ScriptValue.FromNumber(1));

        var elementTemplate = new HostObjectTemplate("Element", nodeTemplate)
            .DefineProperty("tagName", (target, thisVal) => ScriptValue.FromString("DIV"));

        var target = new object();
        var elObj = engine.CreateHostObject(elementTemplate, target);
        engine.SetGlobalProperty("el", elObj);

        // Derived property
        Assert.Equal("DIV", engine.Evaluate("el.tagName").AsString());
        // Prototype property
        Assert.Equal(1, engine.Evaluate("el.nodeType").AsNumber());
    }

    [Fact]
    public void HostObjectDoesNotExposeUnregisteredClrMembers()
    {
        using var engine = new JintScriptEngine();

        var template = new HostObjectTemplate("Person")
            .DefineProperty("name", (target, thisVal) => ScriptValue.FromString(((Person)target).Name));

        var person = new Person { Name = "Alice", Age = 30 };
        var personObj = engine.CreateHostObject(template, person);
        engine.SetGlobalProperty("alice", personObj);

        // Age was NOT registered in template
        Assert.Equal("undefined", engine.Evaluate("typeof alice.age").AsString());
        // Friends was NOT registered
        Assert.Equal("undefined", engine.Evaluate("typeof alice.Friends").AsString());
        // GetType was NOT registered
        Assert.Equal("undefined", engine.Evaluate("typeof alice.GetType").AsString());
    }
}
