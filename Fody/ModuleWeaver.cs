﻿using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.ILAst;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

public class ModuleWeaver
{
    public Action<string> LogInfo { get; set; }
    public Action<string> LogWarning { get; set; }
    public Action<string> LogError { get; set; }
    public ModuleDefinition ModuleDefinition { get; set; }
    public IAssemblyResolver AssemblyResolver { get; set; }
    public string[] DefineConstants { get; set; }

    public ModuleWeaver()
    {
        LogInfo = s => { };
        LogWarning = s => { };
        LogError = s => { };
        DefineConstants = new string[0];
    }

    public void Execute()
    {
        LoggerFactory.LogInfo = LogInfo;
        LoggerFactory.LogWarn = LogWarning;
        LoggerFactory.LogError = LogError;

        var types = ModuleDefinition.GetTypes()
            .ToList();

        foreach (var type in types)
        {
            ProcessType(type);
        }
    }

    private void ProcessType(TypeDefinition type)
    {
        foreach (var method in type.MethodsWithBody())
            ProcessBody(method);

        foreach (var property in type.ConcreteProperties())
        {
            if (property.GetMethod != null)
                ProcessBody(property.GetMethod);
            if (property.SetMethod != null)
                ProcessBody(property.SetMethod);
        }
    }

    private void ProcessBody(MethodDefinition method)
    {
        method.Body.SimplifyMacros();

        var ilProcessor = method.Body.GetILProcessor();

        if (method.Body.Instructions.Last().OpCode == OpCodes.Throw)
            ilProcessor.Append(ilProcessor.Create(OpCodes.Ret));

        method.Body.OptimizeMacros();
        method.Body.SimplifyMacros();

        var ilMethod = Decompile(method);

        var visitor = new UsableVisitor(method);
        visitor.Visit(ilMethod);

        if (!visitor.UsingRanges.Any())
        {
            method.Body.OptimizeMacros();
            return;
        }

        // Convert early returns to branches to the last return
        if (visitor.EarlyReturns.Any() && method.Body.Instructions.Last().OpCode == OpCodes.Ret)
            FixEarlyReturns(method.Body, visitor.EarlyReturns);

        // Inserts nops to ensure nested trys are not overlapping
        var usingBlocks = FixUsingBlocks(ilProcessor,
            visitor.UsingRanges
                .Select(r => Tuple.Create(method.Body.Instructions.AtOffset(r.From), method.Body.Instructions.AtOffset(r.To)))
                .ToList());

        // Add the usings
        foreach (var usingBlock in usingBlocks.OrderBy(u => u.Item1.Offset))
            AddUsing(method.Body, usingBlock);

        method.Body.OptimizeMacros();
        method.Body.SimplifyMacros();

        // Sort exception handlers so inner trys are before outer ones
        method.Body.ExceptionHandlers.ReplaceCollection(method.Body.ExceptionHandlers.OrderBy(x => x, new ExceptionHandlerComparer()));

        // Any branches in a try branching to outside must be converted to leaves
        foreach (var handler in method.Body.ExceptionHandlers)
            ReplaceBranchesWithLeaves(ilProcessor, handler);

        method.Body.OptimizeMacros();
    }

    private static ILBlock Decompile(MethodDefinition method)
    {
        bool inlineVariables = false;

        ILAstBuilder astBuilder = new ILAstBuilder();
        ILBlock ilMethod = new ILBlock();
        DecompilerContext context = new DecompilerContext(method.Module) { CurrentType = method.DeclaringType, CurrentMethod = method };
        ilMethod.Body = astBuilder.Build(method, inlineVariables, context);

        new ILAstOptimizer().Optimize(context, ilMethod, ILAstOptimizationStep.None);
        return ilMethod;
    }

    private void FixEarlyReturns(MethodBody body, List<int> earlyReturns)
    {
        var il = body.GetILProcessor();
        var lastReturn = body.Instructions.Last();
        foreach (var instruction in earlyReturns.Select(offset => body.Instructions.AtOffset(offset)))
        {
            il.Replace(instruction, il.Create(OpCodes.Br, lastReturn));
        }
    }

    private IEnumerable<Tuple<Instruction, Instruction>> FixUsingBlocks(ILProcessor ilProcessor, IEnumerable<Tuple<Instruction, Instruction>> usingBlocks)
    {
        foreach (var groupedEndings in usingBlocks.GroupBy(u => u.Item2))
        {
            Instruction previous = null;
            foreach (var usingBlock in groupedEndings.OrderBy(u => u.Item1.Offset))
            {
                if (previous == null)
                    previous = usingBlock.Item2;
                var nop = ilProcessor.Create(OpCodes.Nop);
                ilProcessor.InsertBefore(previous, nop);
                previous = nop;

                if (usingBlock.Item1 == usingBlock.Item2)
                    yield return Tuple.Create(previous, previous.Next);
                else
                    yield return Tuple.Create(usingBlock.Item1, previous);
            }
        }
    }

    private void AddUsing(MethodBody methodBody, Tuple<Instruction, Instruction> usingBlock)
    {
        var stloc = usingBlock.Item1.Previous;
        while (stloc.OpCode == OpCodes.Nop)
            stloc = stloc.Previous;
        var variable = (VariableDefinition)stloc.Operand;

        if (!variable.VariableType.Resolve().HasInterface("System.IDisposable"))
            return;

        var il = methodBody.GetILProcessor();

        var disposeCall = il.Create(OpCodes.Callvirt, ModuleDefinition.Import(typeof(IDisposable).GetMethod("Dispose")));

        methodBody.OptimizeMacros();
        methodBody.SimplifyMacros();

        if (usingBlock.Item2.Previous.OpCode == OpCodes.Br)
        {
            var leave = il.Create(OpCodes.Leave, (Instruction)usingBlock.Item2.Previous.Operand);
            il.Replace(usingBlock.Item2.Previous, leave);
        }
        else
        {
            var leave = il.Create(OpCodes.Leave, usingBlock.Item2);
            il.InsertBefore(usingBlock.Item2, leave);
        }

        var firstPartOfFinally = il.Create(OpCodes.Ldloc, variable);
        var endFinally = il.Create(OpCodes.Endfinally);

        il.InsertBefore(usingBlock.Item2,
            firstPartOfFinally,
            il.Create(OpCodes.Brfalse, endFinally),
            il.Create(OpCodes.Ldloc, variable),
            disposeCall,
            endFinally
        );

        var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = usingBlock.Item1,
            TryEnd = firstPartOfFinally,
            HandlerStart = firstPartOfFinally,
            HandlerEnd = usingBlock.Item2,
        };

        methodBody.ExceptionHandlers.Add(handler);
    }

    private void ReplaceBranchesWithLeaves(ILProcessor il, ExceptionHandler handler)
    {
        var current = handler.TryStart;
        while (current != handler.TryEnd)
        {
            var next = current.Next;
            if (current.OpCode == OpCodes.Br && ((Instruction)current.Operand).Offset > handler.TryEnd.Offset)
                il.Replace(current, il.Create(OpCodes.Leave, (Instruction)current.Operand));
            current = next;
        }
    }
}