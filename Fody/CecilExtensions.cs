﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.ILAst;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

public static class CecilExtensions
{
    public static IEnumerable<MethodDefinition> MethodsWithBody(this TypeDefinition type)
    {
        return type.Methods.Where(x => x.Body != null);
    }

    public static IEnumerable<PropertyDefinition> ConcreteProperties(this TypeDefinition type)
    {
        return type.Properties.Where(x => (x.GetMethod == null || !x.GetMethod.IsAbstract) && (x.SetMethod == null || !x.SetMethod.IsAbstract));
    }

    public static bool HasInterface(this TypeDefinition type, string interfaceFullName)
    {
        return (type.Interfaces.Any(i => i.FullName.Equals(interfaceFullName))
                || (type.BaseType != null && type.BaseType.Resolve().HasInterface(interfaceFullName)));
    }

    public static void InsertBefore(this ILProcessor processor, Instruction target, params Instruction[] instructions)
    {
        foreach (var instruction in instructions)
            processor.InsertBefore(target, instruction);
    }

    public static void InsertAfter(this ILProcessor processor, Instruction target, params Instruction[] instructions)
    {
        foreach (var instruction in instructions.Reverse())
            processor.InsertAfter(target, instruction);
    }

    public static IEnumerable<Instruction> WithinRange(this Collection<Instruction> instructions, ILRange range)
    {
        return instructions.Where(i => i.Offset >= range.From && i.Offset <= range.To);
    }

    public static Instruction AtOffset(this Collection<Instruction> instructions, int offset)
    {
        return instructions.First(i => i.Offset == offset);
    }

    public static int FirstILOffset(this ILNode node)
    {
        if (node.GetChildren().Any())
            return FirstILOffset(node.GetChildren().First());

        var expression = node as ILExpression;
        if (expression != null)
            return expression.ILRanges.First().From;

        throw new NotSupportedException();
    }

    public static int LastILOffset(this ILNode node)
    {
        var expression = node as ILExpression;
        if (expression != null)
            return expression.ILRanges.Last().To;

        if (node.GetChildren().Any())
            return LastILOffset(node.GetChildren()
                .Where(n => !(n is ILBlock) || ((ILBlock)n).Body.Count > 0)
                .Last());

        throw new NotSupportedException();
    }

    public static void ReplaceCollection<T>(this Collection<T> collection, IEnumerable<T> source)
    {
        var items = source.ToList();
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}