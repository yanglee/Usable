using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.ILAst;
using Mono.Cecil;

public class UsableVisitor : ILNodeVisitor
{
    private readonly Dictionary<ILVariable, int> starts;
    private readonly List<int> currentTrys;

    public UsableVisitor(MethodDefinition method)
    {
        UsingRanges = new List<ILRange>();
        starts = new Dictionary<ILVariable, int>();
        currentTrys = method.Body.ExceptionHandlers.Select(handler => handler.TryStart.Offset).ToList();
    }

    public List<ILRange> UsingRanges { get; private set; }

    protected override ILExpression VisitExpression(ILExpression expression)
    {
        if (expression.Match(ILCode.Stloc))
        {
            var variable = (ILVariable)expression.Operand;

            if (starts.ContainsKey(variable))
            {
                UsingRanges.Add(new ILRange { From = starts[variable], To = expression.FirstILOffset() });
                starts.Remove(variable);
            }

            if (!currentTrys.Contains(expression.LastILOffset()))
                starts.Add(variable, expression.LastILOffset());
        }

        return base.VisitExpression(expression);
    }

    protected override ILBlock VisitBlock(ILBlock block)
    {
        var result = base.VisitBlock(block);

        foreach (var start in starts.Values)
        {
            UsingRanges.Add(new ILRange { From = start, To = block.Body.OfType<ILExpression>().Last().LastILOffset() });
        }

        starts.Clear();

        return result;
    }
}