// Replaces one node (by path Objects/<name>) of file A with the same node of file B, keeping A's object id.
static class Transplant
{
    public static void Run(string a, string b, string output, string objectType)
    {
        List<RoundTrip.N>? donor = null;
        RoundTrip.Run(b, Path.GetTempFileName(), false, nodes => { donor = nodes; return nodes; });
        RoundTrip.Run(a, output, false, nodes =>
        {
            var objsA = nodes.First(n => n.Name == "Objects");
            var objsB = donor!.First(n => n.Name == "Objects");
            var src = objsB.Children.First(n => n.Name == objectType);
            var i = objsA.Children.FindIndex(n => n.Name == objectType);
            var replacement = new RoundTrip.N { Name = src.Name, Children = src.Children, HadSentinel = src.HadSentinel };
            replacement.Props.Add(objsA.Children[i].Props[0]);
            replacement.Props.AddRange(src.Props.Skip(1));
            objsA.Children[i] = replacement;
            return nodes;
        });
    }
}
static class TransplantTop
{
    // Replaces top-level node <name> of file A with B's.
    public static void Run(string a, string b, string output, string name)
    {
        List<RoundTrip.N>? donor = null;
        RoundTrip.Run(b, Path.GetTempFileName(), false, nodes => { donor = nodes; return nodes; });
        RoundTrip.Run(a, output, false, nodes =>
        {
            var i = nodes.FindIndex(n => n.Name == name);
            nodes[i] = donor!.First(n => n.Name == name);
            return nodes;
        });
    }
}
