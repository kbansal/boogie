using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.GraphUtil;

namespace Microsoft.Boogie
{
  public class PolymorphismChecker : ReadOnlyVisitor
  {
    public static bool DoesTypeCtorDeclNeedMonomorphization(TypeCtorDecl typeCtorDecl)
    {
      return typeCtorDecl.Arity > 0 && typeCtorDecl.FindStringAttribute("builtin") == null;
    }

    public static bool IsMonomorphic(Program program)
    {
      var checker = new PolymorphismChecker();
      checker.VisitProgram(program);
      return checker.isMonomorphic;
    }
    
    bool isMonomorphic;

    private PolymorphismChecker()
    {
      isMonomorphic = true;
    }
    
    public override DeclWithFormals VisitDeclWithFormals(DeclWithFormals node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphic = false;
      }
      return base.VisitDeclWithFormals(node);
    }

    public override BinderExpr VisitBinderExpr(BinderExpr node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphic = false;
      }
      return base.VisitBinderExpr(node);
    }

    public override MapType VisitMapType(MapType node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphic = false;
      }
      return base.VisitMapType(node);
    }

    public override Expr VisitNAryExpr(NAryExpr node)
    {
      BinaryOperator op = node.Fun as BinaryOperator;
      if (op != null && op.Op == BinaryOperator.Opcode.Subtype)
      {
        isMonomorphic = false;
      }
      return base.VisitNAryExpr(node);
    }

    public override Declaration VisitTypeCtorDecl(TypeCtorDecl node)
    {
      if (DoesTypeCtorDeclNeedMonomorphization(node))
      {
        isMonomorphic = false;
      }
      return base.VisitTypeCtorDecl(node);
    }
  }

  class TypeDependencyVisitor : ReadOnlyVisitor
  {
    private Graph<TypeVariable> typeVariableDependencyGraph;
    private HashSet<Tuple<TypeVariable, TypeVariable>> strongDependencyEdges;
    private TypeVariable formal;
    private int insideContructedType;
    
    public TypeDependencyVisitor(Graph<TypeVariable> typeVariableDependencyGraph,
      HashSet<Tuple<TypeVariable, TypeVariable>> strongDependencyEdges, TypeVariable formal)
    {
      this.typeVariableDependencyGraph = typeVariableDependencyGraph;
      this.strongDependencyEdges = strongDependencyEdges;
      this.formal = formal;
      this.insideContructedType = 0;
    }

    public override CtorType VisitCtorType(CtorType node)
    {
      insideContructedType++;
      base.VisitCtorType(node);
      insideContructedType--;
      return node;
    }

    public override MapType VisitMapType(MapType node)
    {
      insideContructedType++;
      base.VisitMapType(node);
      insideContructedType--;
      return node;
    }

    public override Type VisitTypeVariable(TypeVariable node)
    {
      typeVariableDependencyGraph.AddEdge(node, formal);
      if (insideContructedType > 0)
      {
        strongDependencyEdges.Add(new Tuple<TypeVariable, TypeVariable>(node, formal));
      }
      return base.VisitTypeVariable(node);
    }
  }
  
  class MonomorphizableChecker : ReadOnlyVisitor
  {
    public static bool IsMonomorphizable(Program program, out Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated, out HashSet<Axiom> polymorphicFunctionAxioms)
    {
      var checker = new MonomorphizableChecker(program);
      checker.VisitProgram(program);
      axiomsToBeInstantiated = checker.axiomsToBeInstantiated;
      polymorphicFunctionAxioms = checker.polymorphicFunctionAxioms;
      return checker.isMonomorphizable && checker.IsFinitelyInstantiable();
    }
    
    private Program program;
    private bool isMonomorphizable;
    private Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated;
    private HashSet<Axiom> polymorphicFunctionAxioms;
    private Graph<TypeVariable> typeVariableDependencyGraph; // (T,U) in this graph iff T flows to U
    private HashSet<Tuple<TypeVariable, TypeVariable>> strongDependencyEdges; // (T,U) in this set iff a type constructed from T flows into U

    private MonomorphizableChecker(Program program)
    {
      this.program = program;
      this.isMonomorphizable = true;
      this.polymorphicFunctionAxioms = program.TopLevelDeclarations.OfType<Function>().Where(f => f.TypeParameters.Count > 0 && f.DefinitionAxiom != null)
        .Select(f => f.DefinitionAxiom).ToHashSet();
      this.axiomsToBeInstantiated = new Dictionary<Axiom, TypeCtorDecl>();
      this.typeVariableDependencyGraph = new Graph<TypeVariable>();
      this.strongDependencyEdges = new HashSet<Tuple<TypeVariable, TypeVariable>>();
    }

    private bool IsFinitelyInstantiable()
    {
      var sccs = new StronglyConnectedComponents<TypeVariable>(typeVariableDependencyGraph.Nodes, typeVariableDependencyGraph.Predecessors, typeVariableDependencyGraph.Successors);
      sccs.Compute();
      foreach (var scc in sccs)
      {
        foreach (var edge in strongDependencyEdges)
        {
          if (scc.Contains(edge.Item1) && scc.Contains(edge.Item2))
          {
            return false;
          }
        }
      }
      return true;
    }
    
    public override BinderExpr VisitBinderExpr(BinderExpr node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphizable = false;
      }
      return base.VisitBinderExpr(node);
    }

    public override Expr VisitNAryExpr(NAryExpr node)
    {
      if (node.Fun is BinaryOperator op && op.Op == BinaryOperator.Opcode.Subtype)
      {
        isMonomorphizable = false;
      }
      else if (node.Fun is FunctionCall functionCall)
      {
        functionCall.Func.TypeParameters.Iter(t =>
        {
          var visitor = new TypeDependencyVisitor(typeVariableDependencyGraph, strongDependencyEdges, t);
          visitor.Visit(node.TypeParameters[t]);
        });
      }
      return base.VisitNAryExpr(node);
    }

    public override MapType VisitMapType(MapType node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphizable = false;
      }
      return base.VisitMapType(node);
    }

    public override Implementation VisitImplementation(Implementation node)
    {
      if (node.TypeParameters.Count > 0)
      {
        isMonomorphizable = false;
        return node;
      }
      return base.VisitImplementation(node);
    }

    private void CheckTypeCtorInstantiatedAxiom(Axiom axiom, string typeCtorName)
    {
      var tcDecl = program.TopLevelDeclarations.OfType<TypeCtorDecl>().FirstOrDefault(tcd => tcd.Name == typeCtorName);
      if (tcDecl == null)
      {
        isMonomorphizable = false;
        return;
      }
      var forallExpr = (ForallExpr) axiom.Expr;
      if (tcDecl.Arity != forallExpr.TypeParameters.Count)
      {
        isMonomorphizable = false;
        return;
      }
      axiomsToBeInstantiated.Add(axiom, tcDecl);
      VisitExpr(forallExpr.Body);
    }

    public override Axiom VisitAxiom(Axiom node)
    {
      if (polymorphicFunctionAxioms.Contains(node))
      {
        return node;
      }
      var typeCtorName = node.FindStringAttribute("ctor");
      if (typeCtorName == null || !(node.Expr is ForallExpr))
      {
        return base.VisitAxiom(node);
      }
      CheckTypeCtorInstantiatedAxiom(node, typeCtorName);
      return node;
    }

    public override Function VisitFunction(Function node)
    {
      if (polymorphicFunctionAxioms.Contains(node.DefinitionAxiom))
      {
        var forallExpr = (ForallExpr) node.DefinitionAxiom.Expr;
        LinqExtender.Map(node.TypeParameters, forallExpr.TypeParameters)
          .Iter(x => typeVariableDependencyGraph.AddEdge(x.Key, x.Value));
        VisitExpr(forallExpr.Body);
      }
      return base.VisitFunction(node);
    }
  }
  
  class MonomorphizationVisitor : StandardVisitor
  {
    class ExprMonomorphizationVisitor : Duplicator
    {
      private MonomorphizationVisitor monomorphizationVisitor;
      private HashSet<Declaration> newInstantiatedDeclarations;
      private Dictionary<TypeVariable, Type> typeParamInstantiation;
      private Dictionary<Variable, Variable> variableMapping;
      private Dictionary<Variable, Variable> boundVarSubst;

      public ExprMonomorphizationVisitor(MonomorphizationVisitor monomorphizationVisitor)
      {
        this.monomorphizationVisitor = monomorphizationVisitor;
        newInstantiatedDeclarations = new HashSet<Declaration>();
        typeParamInstantiation = new Dictionary<TypeVariable, Type>();
        variableMapping = new Dictionary<Variable, Variable>();
        boundVarSubst = new Dictionary<Variable, Variable>();
      }

      public Axiom InstantiateAxiom(Axiom axiom, List<Type> funcTypeParamInstantiations)
      {
        var forallExpr = (ForallExpr) axiom.Expr;
        var savedTypeParamInstantiation = this.typeParamInstantiation;
        this.typeParamInstantiation = LinqExtender.Map(forallExpr.TypeParameters, funcTypeParamInstantiations);
        forallExpr = (ForallExpr) VisitExpr(forallExpr);
        this.typeParamInstantiation = savedTypeParamInstantiation;
        forallExpr.TypeParameters = new List<TypeVariable>();
        var instantiatedAxiom = new Axiom(axiom.tok, forallExpr.Dummies.Count == 0 ? forallExpr.Body : forallExpr,
          axiom.Comment, axiom.Attributes);
        newInstantiatedDeclarations.Add(instantiatedAxiom);
        return instantiatedAxiom;
      }

      public Function InstantiateFunction(Function func, List<Type> funcTypeParamInstantiations)
      {
        if (!monomorphizationVisitor.functionInstantiations[func].ContainsKey(funcTypeParamInstantiations))
        {
          var funcTypeParamInstantiation = LinqExtender.Map(func.TypeParameters, funcTypeParamInstantiations);
          var instantiatedFunction =
            InstantiateFunctionSignature(func, funcTypeParamInstantiations, funcTypeParamInstantiation);
          newInstantiatedDeclarations.Add(instantiatedFunction);
          monomorphizationVisitor.functionInstantiations[func][funcTypeParamInstantiations] = instantiatedFunction;
          if (func.Body != null)
          {
            instantiatedFunction.Body = InstantiateBody(func.Body, funcTypeParamInstantiation,
              LinqExtender.Map(func.InParams, instantiatedFunction.InParams));
          }
          else if (func.DefinitionBody != null)
          {
            instantiatedFunction.DefinitionBody = (NAryExpr) InstantiateBody(func.DefinitionBody,
              funcTypeParamInstantiation, LinqExtender.Map(func.InParams, instantiatedFunction.InParams));
          }
          else if (func.DefinitionAxiom != null)
          {
            instantiatedFunction.DefinitionAxiom =
              this.InstantiateAxiom(func.DefinitionAxiom, funcTypeParamInstantiations);
          }
        }
        return monomorphizationVisitor.functionInstantiations[func][funcTypeParamInstantiations];
      }

      public void AddInstantiatedDeclarations(Program program)
      {
        program.AddTopLevelDeclarations(newInstantiatedDeclarations);
        newInstantiatedDeclarations = new HashSet<Declaration>();
      }

      private Expr InstantiateBody(Expr expr, Dictionary<TypeVariable, Type> funcTypeParamInstantiation,
        Dictionary<Variable, Variable> funcVariableMapping)
      {
        var savedTypeParamInstantiation = this.typeParamInstantiation;
        this.typeParamInstantiation = funcTypeParamInstantiation;
        var savedVariableMapping = this.variableMapping;
        this.variableMapping = funcVariableMapping;
        var newExpr = VisitExpr(expr);
        this.variableMapping = savedVariableMapping;
        this.typeParamInstantiation = savedTypeParamInstantiation;
        return newExpr;
      }

      private Function InstantiateFunctionSignature(Function func, List<Type> funcTypeParamInstantiations,
        Dictionary<TypeVariable, Type> funcTypeParamInstantiation)
      {
        var savedTypeParamInstantiation = this.typeParamInstantiation;
        this.typeParamInstantiation = funcTypeParamInstantiation;
        var instantiatedInParams =
          func.InParams.Select(x =>
              new Formal(x.tok, new TypedIdent(x.TypedIdent.tok, x.TypedIdent.Name, VisitType(x.TypedIdent.Type)),
                true))
            .ToList<Variable>();
        var instantiatedOutParams =
          func.OutParams.Select(x =>
              new Formal(x.tok, new TypedIdent(x.TypedIdent.tok, x.TypedIdent.Name, VisitType(x.TypedIdent.Type)),
                false))
            .ToList<Variable>();
        var instantiatedFunction = new Function(
          func.tok,
          MkInstanceName(func.Name, funcTypeParamInstantiations),
          new List<TypeVariable>(),
          instantiatedInParams,
          instantiatedOutParams.First(),
          func.Comment,
          func.Attributes);
        this.typeParamInstantiation = savedTypeParamInstantiation;
        return instantiatedFunction;
      }

      private void InstantiateType(TypeCtorDecl typeCtorDecl, List<Type> typeParamInstantiations)
      {
        if (!monomorphizationVisitor.typeInstantiations[typeCtorDecl].ContainsKey(typeParamInstantiations))
        {
          if (typeCtorDecl is DatatypeTypeCtorDecl datatypeTypeCtorDecl)
          {
            var newDatatypeTypeCtorDecl = new DatatypeTypeCtorDecl(
              new TypeCtorDecl(datatypeTypeCtorDecl.tok,
                MkInstanceName(datatypeTypeCtorDecl.Name, typeParamInstantiations), 0,
                datatypeTypeCtorDecl.Attributes));
            newInstantiatedDeclarations.Add(newDatatypeTypeCtorDecl);
            monomorphizationVisitor.typeInstantiations[datatypeTypeCtorDecl]
              .Add(typeParamInstantiations, newDatatypeTypeCtorDecl);
            datatypeTypeCtorDecl.Constructors.Iter(constructor =>
              InstantiateDatatypeConstructor(newDatatypeTypeCtorDecl, constructor, typeParamInstantiations));
          }
          else
          {
            var newTypeCtorDecl =
              new TypeCtorDecl(typeCtorDecl.tok, MkInstanceName(typeCtorDecl.Name, typeParamInstantiations), 0,
                typeCtorDecl.Attributes);
            newInstantiatedDeclarations.Add(newTypeCtorDecl);
            monomorphizationVisitor.typeInstantiations[typeCtorDecl].Add(typeParamInstantiations, newTypeCtorDecl);
          }
        }
      }

      private void InstantiateDatatypeConstructor(DatatypeTypeCtorDecl newDatatypeTypeCtorDecl,
        DatatypeConstructor constructor, List<Type> typeParamInstantiations)
      {
        var newConstructor = new DatatypeConstructor(newDatatypeTypeCtorDecl,
          InstantiateFunctionSignature(constructor, typeParamInstantiations,
            LinqExtender.Map(constructor.TypeParameters, typeParamInstantiations)));
        newConstructor.membership = DatatypeMembership.NewDatatypeMembership(newConstructor);
        for (int i = 0; i < newConstructor.InParams.Count; i++)
        {
          newConstructor.selectors.Add(DatatypeSelector.NewDatatypeSelector(newConstructor, i));
        }
      }

      private static string MkInstanceName(string name, List<Type> typeParamInstantiations)
      {
        typeParamInstantiations.Iter(x => name = $"{name}_{x.UniqueId}");
        return name;
      }
      
      private Type LookupType(Type type)
      {
        if (type is CtorType ctorType && PolymorphismChecker.DoesTypeCtorDeclNeedMonomorphization(ctorType.Decl))
        {
          return new CtorType(Token.NoToken, monomorphizationVisitor.typeInstantiations[ctorType.Decl][ctorType.Arguments],
            new List<Type>());
        }
        else
        {
          return type;
        }
      }

      public override Expr VisitNAryExpr(NAryExpr node)
      {
        var returnExpr = (NAryExpr) base.VisitNAryExpr(node);
        if (returnExpr.Fun is TypeCoercion)
        {
          return returnExpr.Args[0];
        }

        if (returnExpr.Fun is FunctionCall functionCall)
        {
          // a non-generic function must be processed to rewrite any generic types it uses
          // to the corresponding instantiated types
          if (functionCall.Func.TypeParameters.Count == 0)
          {
            if (functionCall.Func is DatatypeMembership membership)
            {
              monomorphizationVisitor.VisitTypeCtorDecl(membership.constructor.datatypeTypeCtorDecl);
            }
            else if (functionCall.Func is DatatypeSelector selector)
            {
              monomorphizationVisitor.VisitTypeCtorDecl(selector.constructor.datatypeTypeCtorDecl);
            }
            else if (functionCall.Func is DatatypeConstructor constructor)
            {
              monomorphizationVisitor.VisitTypeCtorDecl(constructor.datatypeTypeCtorDecl);
            }
            else
            {
              monomorphizationVisitor.VisitFunction(functionCall.Func);
            }
            returnExpr.Fun = new FunctionCall(functionCall.Func);
          }
          else
          {
            var typeParamInstantiations =
              returnExpr.TypeParameters.FormalTypeParams.Select(x =>
                  TypeProxy.FollowProxy(returnExpr.TypeParameters[x]).Substitute(typeParamInstantiation))
                .Select(x => LookupType(x)).ToList();
            if (functionCall.Func is DatatypeMembership membership)
            {
              InstantiateType(membership.constructor.datatypeTypeCtorDecl, typeParamInstantiations);
              var datatypeTypeCtorDecl =
                (DatatypeTypeCtorDecl) monomorphizationVisitor.typeInstantiations[membership.constructor.datatypeTypeCtorDecl][typeParamInstantiations];
              returnExpr.Fun =
                new FunctionCall(datatypeTypeCtorDecl.Constructors[membership.constructor.index].membership);
            }
            else if (functionCall.Func is DatatypeSelector selector)
            {
              InstantiateType(selector.constructor.datatypeTypeCtorDecl, typeParamInstantiations);
              var datatypeTypeCtorDecl =
                (DatatypeTypeCtorDecl) monomorphizationVisitor.typeInstantiations[selector.constructor.datatypeTypeCtorDecl][typeParamInstantiations];
              returnExpr.Fun = new FunctionCall(datatypeTypeCtorDecl.Constructors[selector.constructor.index]
                .selectors[selector.index]);
            }
            else if (functionCall.Func is DatatypeConstructor constructor)
            {
              InstantiateType(constructor.datatypeTypeCtorDecl, typeParamInstantiations);
              var datatypeTypeCtorDecl =
                (DatatypeTypeCtorDecl) monomorphizationVisitor.typeInstantiations[constructor.datatypeTypeCtorDecl][typeParamInstantiations];
              returnExpr.Fun = new FunctionCall(datatypeTypeCtorDecl.Constructors[constructor.index]);
            }
            else
            {
              returnExpr.Fun = new FunctionCall(InstantiateFunction(functionCall.Func, typeParamInstantiations));
            }
            returnExpr.TypeParameters = SimpleTypeParamInstantiation.EMPTY;
            returnExpr.Type = TypeProxy.FollowProxy(returnExpr.Type);
          }
        }

        return returnExpr;
      }

      public override Type VisitTypeVariable(TypeVariable node)
      {
        if (typeParamInstantiation.Count == 0)
        {
          return node;
        }

        return typeParamInstantiation[node];
      }

      public override MapType VisitMapType(MapType node)
      {
        node = (MapType) node.Clone();
        for (int i = 0; i < node.Arguments.Count; ++i)
        {
          node.Arguments[i] = (Type) this.Visit(node.Arguments[i]);
        }

        node.Result = (Type) this.Visit(node.Result);
        return node;
      }

      public override CtorType VisitCtorType(CtorType node)
      {
        node = (CtorType) node.Clone();
        for (int i = 0; i < node.Arguments.Count; ++i)
        {
          node.Arguments[i] = (Type) this.Visit(node.Arguments[i]);
        }
        var typeCtorDecl = node.Decl;
        if (monomorphizationVisitor.triggerTypes.ContainsKey(typeCtorDecl) && !monomorphizationVisitor.triggerTypes[typeCtorDecl].Contains(node))
        {
          monomorphizationVisitor.triggerTypes[typeCtorDecl].Add(node);
          monomorphizationVisitor.newTriggerTypes[typeCtorDecl].Add(node);
        }
        if (PolymorphismChecker.DoesTypeCtorDeclNeedMonomorphization(typeCtorDecl))
        {
          InstantiateType(typeCtorDecl, node.Arguments);
          return new CtorType(node.tok, monomorphizationVisitor.typeInstantiations[typeCtorDecl][node.Arguments],
            new List<Type>());
        }
        return node;
      }

      public override Type VisitType(Type node)
      {
        return (Type) Visit(node);
      }

      public override Expr VisitExpr(Expr node)
      {
        node = base.VisitExpr(node);
        node.Type = VisitType(node.Type);
        return node;
      }

      public override Expr VisitIdentifierExpr(IdentifierExpr node)
      {
        if (variableMapping.ContainsKey(node.Decl))
        {
          return new IdentifierExpr(node.tok, variableMapping[node.Decl], node.Immutable);
        }

        if (boundVarSubst.ContainsKey(node.Decl))
        {
          return new IdentifierExpr(node.tok, boundVarSubst[node.Decl], node.Immutable);
        }

        return base.VisitIdentifierExpr(node);
      }

      public override BinderExpr VisitBinderExpr(BinderExpr node)
      {
        var oldToNew = node.Dummies.ToDictionary(x => x,
          x => new BoundVariable(x.tok, new TypedIdent(x.tok, x.Name, VisitType(x.TypedIdent.Type)),
            x.Attributes));
        foreach (var x in node.Dummies)
        {
          boundVarSubst.Add(x, oldToNew[x]);
        }

        BinderExpr expr = base.VisitBinderExpr(node);
        expr.Dummies = node.Dummies.Select(x => oldToNew[x]).ToList<Variable>();
        // We process triggers of quantifier expressions here, because otherwise the
        // substitutions for bound variables have to be leaked outside this procedure.
        if (node is QuantifierExpr quantifierExpr)
        {
          if (quantifierExpr.Triggers != null)
          {
            ((QuantifierExpr) expr).Triggers = this.VisitTrigger(quantifierExpr.Triggers);
          }
        }

        foreach (var x in node.Dummies)
        {
          boundVarSubst.Remove(x);
        }

        return expr;
      }

      public override QuantifierExpr VisitQuantifierExpr(QuantifierExpr node)
      {
        // Don't remove this implementation! Triggers should be duplicated in VisitBinderExpr.
        return (QuantifierExpr) this.VisitBinderExpr(node);
      }

      public override Expr VisitLetExpr(LetExpr node)
      {
        var oldToNew = node.Dummies.ToDictionary(x => x,
          x => new BoundVariable(x.tok, new TypedIdent(x.tok, x.Name, VisitType(x.TypedIdent.Type)),
            x.Attributes));
        foreach (var x in node.Dummies)
        {
          boundVarSubst.Add(x, oldToNew[x]);
        }

        var expr = (LetExpr) base.VisitLetExpr(node);
        expr.Dummies = node.Dummies.Select(x => oldToNew[x]).ToList<Variable>();
        foreach (var x in node.Dummies)
        {
          boundVarSubst.Remove(x);
        }

        return expr;
      }
    }

    public static MonomorphizationVisitor Initialize(Program program, Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated,
      HashSet<Axiom> polymorphicFunctionAxioms)
    {
      var monomorphizationVisitor = new MonomorphizationVisitor(program, axiomsToBeInstantiated, polymorphicFunctionAxioms);
      monomorphizationVisitor.VisitProgram(program);
      monomorphizationVisitor.InstantiateAxioms();
      monomorphizationVisitor.exprMonomorphizationVisitor.AddInstantiatedDeclarations(program);
      Contract.Assert(PolymorphismChecker.IsMonomorphic(program));
      return monomorphizationVisitor;
    }
    
    public Function Monomorphize(string functionName, Dictionary<string, Type> typeParamInstantiationMap)
    {
      if (!nameToFunction.ContainsKey(functionName))
      {
        return null;
      }
      var function = nameToFunction[functionName];
      var typeParamInstantiations = function.TypeParameters.Select(tp => typeParamInstantiationMap[tp.Name]).ToList();
      var instantiatedFunction = exprMonomorphizationVisitor.InstantiateFunction(function, typeParamInstantiations);
      InstantiateAxioms();
      exprMonomorphizationVisitor.AddInstantiatedDeclarations(program);
      return instantiatedFunction;
    }

    private Program program;
    private Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated;
    private Dictionary<string, Function> nameToFunction;
    private Dictionary<Function, Dictionary<List<Type>, Function>> functionInstantiations;
    private Dictionary<TypeCtorDecl, Dictionary<List<Type>, TypeCtorDecl>> typeInstantiations;
    private Dictionary<TypeCtorDecl, HashSet<CtorType>> triggerTypes;
    private Dictionary<TypeCtorDecl, HashSet<CtorType>> newTriggerTypes;
    private HashSet<TypeCtorDecl> visitedTypeCtorDecls;
    private HashSet<Function> visitedFunctions;
    private ExprMonomorphizationVisitor exprMonomorphizationVisitor;
    
    private MonomorphizationVisitor(Program program, Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated, HashSet<Axiom> polymorphicFunctionAxioms)
    {
      this.program = program;
      this.axiomsToBeInstantiated = axiomsToBeInstantiated;
      functionInstantiations = new Dictionary<Function, Dictionary<List<Type>, Function>>();
      nameToFunction = new Dictionary<string, Function>();
      program.TopLevelDeclarations.OfType<Function>().Where(function => function.TypeParameters.Count > 0).Iter(
        function =>
        {
          nameToFunction.Add(function.Name, function);
          functionInstantiations.Add(function, new Dictionary<List<Type>, Function>(new ListComparer<Type>()));
        });
      typeInstantiations = new Dictionary<TypeCtorDecl, Dictionary<List<Type>, TypeCtorDecl>>();
      program.TopLevelDeclarations.OfType<TypeCtorDecl>()
        .Where(typeCtorDecl => PolymorphismChecker.DoesTypeCtorDeclNeedMonomorphization(typeCtorDecl)).Iter(
          typeCtorDecl =>
            typeInstantiations.Add(typeCtorDecl, new Dictionary<List<Type>, TypeCtorDecl>(new ListComparer<Type>())));
      triggerTypes = new Dictionary<TypeCtorDecl, HashSet<CtorType>>();
      newTriggerTypes = new Dictionary<TypeCtorDecl, HashSet<CtorType>>();
      axiomsToBeInstantiated.Values.ToHashSet().Iter(typeCtorDecl =>
      {
        triggerTypes.Add(typeCtorDecl, new HashSet<CtorType>());
        newTriggerTypes.Add(typeCtorDecl, new HashSet<CtorType>());
      });
      this.visitedTypeCtorDecls = new HashSet<TypeCtorDecl>();
      this.visitedFunctions = new HashSet<Function>();
      exprMonomorphizationVisitor = new ExprMonomorphizationVisitor(this);

      program.RemoveTopLevelDeclarations(decl => 
        decl is Function function && functionInstantiations.ContainsKey(function) ||
        decl is TypeCtorDecl typeCtorDecl && typeInstantiations.ContainsKey(typeCtorDecl) ||
        decl is Axiom axiom && (axiomsToBeInstantiated.ContainsKey(axiom) || polymorphicFunctionAxioms.Contains(axiom)));
    }

    public void InstantiateAxioms()
    {
      while (newTriggerTypes.Any(x => x.Value.Count != 0))
      {
        var nextTriggerTypes = this.newTriggerTypes;
        newTriggerTypes = new Dictionary<TypeCtorDecl, HashSet<CtorType>>();
        nextTriggerTypes.Iter(x => { newTriggerTypes.Add(x.Key, new HashSet<CtorType>()); });
        foreach ((var axiom, var tcDecl) in axiomsToBeInstantiated)
        {
          nextTriggerTypes[tcDecl].Iter(trigger => exprMonomorphizationVisitor.InstantiateAxiom(axiom, trigger.Arguments));
        }
      }
    }

    public override CtorType VisitCtorType(CtorType node)
    {
      return (CtorType) exprMonomorphizationVisitor.VisitType(node);
    }
    
    public override Expr VisitExpr(Expr node)
    {
      return exprMonomorphizationVisitor.VisitExpr(node);
    }

    public override Expr VisitIdentifierExpr(IdentifierExpr node)
    {
      return exprMonomorphizationVisitor.VisitExpr(node);
    }

    // this function may be called directly by exprMonomorphizationVisitor
    // if a non-generic call to a datatype constructor/selector/membership
    // is discovered in an expression
    public override Declaration VisitTypeCtorDecl(TypeCtorDecl node)
    {
      if (visitedTypeCtorDecls.Contains(node))
      {
        return node;
      }
      visitedTypeCtorDecls.Add(node);
      if (node is DatatypeTypeCtorDecl datatypeTypeCtorDecl)
      {
        datatypeTypeCtorDecl.Constructors.Iter(constructor => VisitConstructor(constructor));
      }
      return base.VisitTypeCtorDecl(node);
    }

    private void VisitConstructor(DatatypeConstructor constructor)
    {
      base.VisitFunction(constructor);
      base.VisitFunction(constructor.membership);
      constructor.selectors.Iter(selector => base.VisitFunction(selector));
    }
    
    public override Absy Visit(Absy node)
    {
      if (node is ICarriesAttributes attrNode && attrNode.Attributes != null)
      {
        VisitQKeyValue(attrNode.Attributes);
      }
      return base.Visit(node);
    }

    // this function may be called directly by exprMonomorphizationVisitor
    // if a non-generic function call is discovered in an expression
    public override Function VisitFunction(Function node)
    {
      if (visitedFunctions.Contains(node))
      {
        return node;
      }
      visitedFunctions.Add(node);
      return base.VisitFunction(node);
    }
  }
  
  public class Monomorphizer
  {
    public static bool Monomorphize(Program program)
    {
      Dictionary<Axiom, TypeCtorDecl> axiomsToBeInstantiated;
      HashSet<Axiom> polymorphicFunctionAxioms;
      var isMonomorphizable = MonomorphizableChecker.IsMonomorphizable(program, out axiomsToBeInstantiated, out polymorphicFunctionAxioms);
      if (isMonomorphizable)
      {
        var monomorphizationVisitor = MonomorphizationVisitor.Initialize(program, axiomsToBeInstantiated, polymorphicFunctionAxioms);
        program.monomorphizer = new Monomorphizer(monomorphizationVisitor);
        return true;
      }
      return false;
    }

    public Function Monomorphize(string functionName, Dictionary<string, Type> typeParamInstantiationMap)
    {
      return monomorphizationVisitor.Monomorphize(functionName, typeParamInstantiationMap);
    }

    private MonomorphizationVisitor monomorphizationVisitor;
    private Monomorphizer(MonomorphizationVisitor monomorphizationVisitor)
    {
      this.monomorphizationVisitor = monomorphizationVisitor;
    }
  }
}