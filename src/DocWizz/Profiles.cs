// Built-in documentation profiles: which sections a symbol must document. Patterns match in order; the first match
// decides the sections, and `level` raises the requirement to at least that. Profiles oriented on a standard only
// borrow its vocabulary — DocWizz reports coverage against the profile, never compliance with the standard.
//
// Sections: summary, param, returns, exception, example, remarks (written doc comments), and derived from the code:
// dependencies, endpoint, authorization, input, output, events, state (facts), side_effects (inferred).
// `param`/`returns`/`exception`/`input`/`output`/`events`/`state`/`endpoint` only apply when the symbol has them.
static class Profiles
{
    public static string[] Names => [.. All.Keys];

    static readonly Dictionary<string, string> All = new()
    {
        ["default"] = """
            patterns:
              endpoint:   { match: { tag: endpoint }, level: high, sections: [summary, param, input, output, authorization] }
              controller: { match: { tag: controller }, level: high, sections: [summary, dependencies] }
              service:    { match: { type: "*Service" }, sections: [summary, param, dependencies, side_effects] }
              component:  { match: { kind: component }, sections: [summary, param, dependencies, events, state] }   # param = every prop has a /** doc */
              default:    { sections: [summary] }
            """,
        // Reference documentation for developers: full signatures, effects, failure modes.
        ["software"] = """
            patterns:
              endpoint:   { match: { tag: endpoint }, level: high, sections: [summary, param, returns, exception, authorization] }
              controller: { match: { tag: controller }, level: high, sections: [summary, dependencies] }
              service:    { match: { type: "*Service" }, sections: [summary, param, returns, exception, side_effects] }
              component:  { match: { kind: component }, sections: [summary, param, events] }
              default:    { sections: [summary, param, returns, exception] }
            """,
        // Consumers of the HTTP API: every endpoint's contract.
        ["api"] = """
            patterns:
              endpoint:   { match: { tag: endpoint }, level: high, sections: [summary, param, input, output, authorization, exception] }
              controller: { match: { tag: controller }, level: high, sections: [summary, endpoint, authorization] }
              default:    { sections: [summary] }
            """,
        // Building blocks and how they connect, rather than every member.
        ["architecture"] = """
            patterns:
              controller: { match: { tag: controller }, level: high, sections: [summary, dependencies, endpoint] }
              service:    { match: { kind: class, name: "*Service" }, level: medium, sections: [summary, dependencies, side_effects] }
              interface:  { match: { kind: interface }, level: medium, sections: [summary] }
              component:  { match: { kind: component }, level: medium, sections: [summary, dependencies, state] }
              store:      { match: { kind: store }, level: medium, sections: [summary, dependencies] }
              default:    { sections: [summary] }
            """,
        // Task-oriented documentation for readers outside the code (ISO/IEC/IEEE 26514, S1000D style): examples required.
        ["technical-publication"] = """
            patterns:
              endpoint:   { match: { tag: endpoint }, level: high, sections: [summary, param, input, output, example] }
              component:  { match: { kind: component }, sections: [summary, param, events, example] }
              default:    { sections: [summary, remarks, example] }
            """,
        // Oriented on ISO/IEC/IEEE 42010 architecture descriptions: architecture profile + human-authored AD sections.
        ["iso-42010"] = """
            patterns:
              controller: { match: { tag: controller }, level: high, sections: [summary, dependencies, endpoint] }
              service:    { match: { kind: class, name: "*Service" }, level: medium, sections: [summary, dependencies, side_effects] }
              interface:  { match: { kind: interface }, level: medium, sections: [summary] }
              component:  { match: { kind: component }, level: medium, sections: [summary, dependencies, state] }
              store:      { match: { kind: store }, level: medium, sections: [summary, dependencies] }
              default:    { sections: [summary] }
            architecture_sections: [stakeholders, concerns, decisions, deployment, security]
            """,
        // Oriented on ISO/IEC/IEEE 15289 information items (software design / interface description content).
        ["iso-15289"] = """
            patterns:
              endpoint:   { match: { tag: endpoint }, level: high, sections: [summary, param, input, output, authorization, exception] }
              controller: { match: { tag: controller }, level: high, sections: [summary, dependencies] }
              interface:  { match: { kind: interface }, level: medium, sections: [summary] }
              service:    { match: { type: "*Service" }, sections: [summary, param, returns, exception, side_effects] }
              default:    { sections: [summary, param, returns, exception] }
            architecture_sections: [stakeholders, concerns, decisions]
            """,
    };

    public static string Yaml(string name) => All.TryGetValue(name, out var y) ? y
        : throw new ArgumentException($"unknown profile '{name}' ({string.Join(", ", Names)})");
}
