﻿// For conditions of distribution and use, see copyright notice in LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace BindingsGenerator
{
    struct Overload
    {
        public string functionName;
        public Symbol function;
        public List<Parameter> parameters;
    }

    struct Property
    {
        public string name;
        public bool readOnly;
    }

    class Program
    {
        static HashSet<string> classNames = new HashSet<string>();
        static List<string> exposeTheseClasses = new List<string>();
        static string fileBasePath = "";
        static Dictionary<string, string> classHeaderFiles = new Dictionary<string, string>();
        static string[] headerFiles = null;

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("First cmdline parameter should be the absolute path to the root where doxygen generated the documentation XML files.");
                Console.WriteLine("Second cmdline parameter should be the output directory.");
                Console.WriteLine("Third cmdline parameter (optional) should be the root path of the exposed code. If omitted, include paths may not be accurate.");
                Console.WriteLine("Further cmdline parameters can optionally list the classes to be exposed. Otherwise all will be exposed. When amount of classes is limited, functions using excluded classes will not be exposed.");
                return;
            }

            CodeStructure s = new CodeStructure();
            s.LoadSymbolsFromDirectory(args[0], true);

            string outputDirectory = args[1];
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception)
            {
            }

            if (args.Length > 2)
            {
                fileBasePath = args[2];
                headerFiles = Directory.GetFiles(fileBasePath, "*.h", SearchOption.AllDirectories);
            }

            if (args.Length > 3)
            {
                for (int i = 3; i < args.Length; ++i)
                    exposeTheseClasses.Add(args[i]);
            }

            foreach (Symbol classSymbol in s.symbolsByName.Values)
            {
                if (classSymbol.kind == "class" && (exposeTheseClasses.Count == 0 || exposeTheseClasses.Contains(StripNamespace(classSymbol.name))))
                {
                    classNames.Add(classSymbol.name);
                    if (headerFiles != null)
                    {
                        foreach (string str in headerFiles)
                        {
                            if (str.EndsWith(classSymbol.name + ".h"))
                            {
                                string sanitated = str.Substring(fileBasePath.Length).Replace('\\', '/');
                                if (sanitated.StartsWith("/"))
                                    sanitated = sanitated.Substring(1);
                                classHeaderFiles[classSymbol.name] = sanitated;
                                break;
                            }
                        }
                    }
                }
            }

            foreach (Symbol classSymbol in s.symbolsByName.Values)
            {
                if (classNames.Contains(classSymbol.name))
                    GenerateClassBindings(classSymbol, outputDirectory);
            }
        }

        static void GenerateClassBindings(Symbol classSymbol, string outputDirectory)
        {
            string className = StripNamespace(classSymbol.name);
            string namespaceName = ExtractNamespace(classSymbol.name);

            Console.WriteLine("Generating bindings for " + className);

            TextWriter tw = new StreamWriter(outputDirectory + "/" + className + "Bindings.cpp");
            tw.WriteLine("// For conditions of distribution and use, see copyright notice in LICENSE");
            tw.WriteLine("// This file has been autogenerated with BindingsGenerator");
            tw.WriteLine("");
            tw.WriteLine("#include \"StableHeaders.h\"");
            tw.WriteLine("#include \"CoreTypes.h\"");
            tw.WriteLine("#include \"BindingsHelpers.h\"");
            tw.WriteLine("#include \"" + FindIncludeForClass(classSymbol.name) + "\"");
            // Find dependency classes and refer to them
            HashSet<string> dependencies = FindDependencies(classSymbol);
            // Includes
            foreach (string s in dependencies)
                tw.WriteLine("#include \"" + FindIncludeForClass(s) + "\"");
            tw.WriteLine("");

            if (namespaceName.Length > 0)
            {
                tw.WriteLine("");
                tw.WriteLine("using namespace " + namespaceName + ";");
                if (namespaceName != "Tundra")
                    tw.WriteLine("using namespace Tundra;");
            }
            tw.WriteLine("using namespace std;");
            tw.WriteLine("");

            tw.WriteLine("namespace JSBindings");
            tw.WriteLine("{");
            tw.WriteLine("");
            // Externs for the type identifiers and definitions for dependency classes' destructors
            foreach (string s in dependencies)
            {
                tw.WriteLine("extern const char* " + ClassIdentifier(s) + ";");
            }
            tw.WriteLine("");
            foreach (string s in dependencies)
            {
                tw.WriteLine("duk_ret_t " + s + "_Finalizer" + DukSignature() + ";");
            }
            tw.WriteLine("");

            // Own type identifier
            tw.WriteLine("const char* " + ClassIdentifier(className) + " = \"" + className + "\";");
            tw.WriteLine("");

            // \todo Handle refcounted class destruction (wrap in a smart ptr)
            Dictionary<string, List<Overload> > overloads = new Dictionary<string, List<Overload> >();
            Dictionary<string, List<Overload>> staticOverloads = new Dictionary<string, List<Overload>>();
            List<Property> properties = new List<Property>();
            GenerateFinalizer(classSymbol, tw);
            GeneratePropertyAccessors(classSymbol, tw, properties);
            GenerateMemberFunctions(classSymbol, tw, overloads, false);
            GenerateFunctionSelectors(classSymbol, tw, overloads);
            GenerateMemberFunctions(classSymbol, tw, staticOverloads, true);
            GenerateFunctionSelectors(classSymbol, tw, staticOverloads);
            GenerateFunctionList(classSymbol, tw, overloads, false);
            GenerateFunctionList(classSymbol, tw, staticOverloads, true);
            GenerateExposeFunction(classSymbol, tw, overloads, staticOverloads, properties);
       
            // \todo Create bindings for static functions
            // \todo Create code to instantiate the JS constructor + prototype

            tw.WriteLine("}");
            tw.Close();
        }

        static string FindIncludeForClass(string name)
        {
            name = StripNamespace(name);
            if (classHeaderFiles.ContainsKey(name))
                return classHeaderFiles[name];
            else
                return name + ".h";
        }

        static string ExtractNamespace(string className)
        {
            int separatorIndex = className.LastIndexOf("::");
            if (separatorIndex > 0)
                return className.Substring(0, separatorIndex);
            else
                return "";
        }

        static string StripNamespace(string className)
        {
            int separatorIndex = className.LastIndexOf("::");
            if (separatorIndex > 0)
                return className.Substring(separatorIndex + 2);
            else
                return className;
        }

        static string GenerateGetFromStack(Symbol classSymbol, int stackIndex, string varName, bool nullCheck = false)
        {
            string typeName = classSymbol.type;
            if (typeName == null || typeName.Length == 0)
                typeName = classSymbol.name;

            return GenerateGetFromStack(typeName, stackIndex, varName, nullCheck);
        }

        static string GenerateGetFromStack(string typeName, int stackIndex, string varName, bool nullCheck = false)
        {
            typeName = SanitateTypeName(typeName);

            if (Symbol.IsNumberType(typeName))
            {
                if (typeName == "double")
                    return typeName + " " + varName + " = duk_require_number(ctx, " + stackIndex + ");";
                else
                    return typeName + " " + varName + " = (" + typeName + ")duk_require_number(ctx, " + stackIndex + ");"; 
            }
            else if (typeName == "bool")
                return typeName + " " + varName + " = duk_require_bool(ctx, " + stackIndex + ");";
            else if (typeName == "string")
                return typeName + " " + varName + "(duk_require_string(ctx, " + stackIndex + "));";
            else if (typeName == "String")
                return typeName + " " + varName + "(duk_require_string(ctx, " + stackIndex + "));";
            else if (!Symbol.IsPODType(typeName))
            {
                if (!nullCheck)
                    return typeName + "* " + varName + " = GetObject<" + typeName + ">(ctx, " + stackIndex + ", " + ClassIdentifier(typeName) + ");";
                else
                    return typeName + "* " + varName + " = GetCheckedObject<" + typeName + ">(ctx, " + stackIndex + ", " + ClassIdentifier(typeName) + ");";
            }
            else
                throw new System.Exception("Unsupported type " + typeName + " for GenerateGetVariable()!");
        }

        static string GeneratePushToStack(Symbol classSymbol, string source)
        {
            string typeName = classSymbol.type;
            if (typeName == null || typeName.Length == 0)
                typeName = classSymbol.name;

            return GeneratePushToStack(typeName, source);
        }

        static string GeneratePushToStack(string typeName, string source)
        {
            typeName = SanitateTypeName(typeName);

            if (Symbol.IsNumberType(typeName))
                return "duk_push_number(ctx, " + source + ");";
            else if (typeName == "bool")
                return "duk_push_boolean(ctx, " + source + ");";
            else if (typeName == "string")
                return "duk_push_string(ctx, " + source + ".c_str());";
            else if (typeName == "String")
                return "duk_push_string(ctx, " + source + ".c_str());";
            else
            {
                typeName = SanitateTypeName(typeName);
                return "PushValueObjectCopy<" + typeName + ">(ctx, " + source + ", " + ClassIdentifier(typeName) + ", " + typeName + "_Finalizer);";
            }
        }

        static string GeneratePushConstructorResultToStack(string typeName, string source)
        {
            return "PushConstructorResult<" + typeName + ">(ctx, " + source + ", " + ClassIdentifier(typeName) + ", " + typeName + "_Finalizer);";
        }

        static string GenerateGetThis(Symbol classSymbol, string varName = "thisObj")
        {
            string typeName = classSymbol.type;
            if (typeName == null || typeName.Length == 0)
                typeName = classSymbol.name;
            typeName = StripNamespace(typeName);

            return typeName + "* " + varName + " = GetThisObject<" + typeName + ">(ctx, " + ClassIdentifier(typeName) + ");";
        }

        static string GenerateArgCheck(Parameter p, int stackIndex)
        {
            string typeName = SanitateTypeName(p.BasicType());

            if (Symbol.IsNumberType(typeName))
                return "duk_is_number(ctx, " + stackIndex + ")";
            else if (typeName == "bool")
                return "duk_is_boolean(ctx, " + stackIndex + ")";
            else if (typeName == "string" || typeName == "String")
                return "duk_is_string(ctx, " + stackIndex + ")";
            if (!Symbol.IsPODType(typeName))
                return "GetObject<" + typeName + ">(ctx, " + stackIndex + ", " + ClassIdentifier(typeName) + ")";
            else
                throw new System.Exception("Unsupported type " + typeName + " for GenerateArgCheck()!");
        }

        static void GenerateFinalizer(Symbol classSymbol, TextWriter tw)
        {
            tw.WriteLine("duk_ret_t " + StripNamespace(classSymbol.name) + "_Finalizer" + DukSignature());
            tw.WriteLine("{");
            tw.WriteLine(Indent(1) + GenerateGetFromStack(classSymbol, 0, "obj"));
            tw.WriteLine(Indent(1) + "if (obj)");
            tw.WriteLine(Indent(1) + "{");
            tw.WriteLine(Indent(2) + "delete obj;");
            tw.WriteLine(Indent(2) + "SetObject(ctx, 0, 0, " + ClassIdentifier(classSymbol.name) + ");");
            tw.WriteLine(Indent(1) + "}");
            tw.WriteLine(Indent(1) + "return 0;");
            tw.WriteLine("}");
            tw.WriteLine("");
        }

        static void GeneratePropertyAccessors(Symbol classSymbol, TextWriter tw, List<Property> properties)
        {
            string className = StripNamespace(classSymbol.name);

            // \hack MGL Frustum class contains private members inside unnamed unions, which CodeStructure doesn't recognize as private. Skip properties altogether for Frustum.
            if (classSymbol.name == "Frustum")
                return;
            
            foreach (Symbol child in classSymbol.children)
            {
                // \todo Handle non-POD accessors
                if (child.kind == "variable" && !child.isStatic && IsScriptable(child) && child.visibilityLevel == VisibilityLevel.Public && Symbol.IsPODType(child.type))
                {
                    Property newProperty;
                    newProperty.name = child.name;
                    newProperty.readOnly = true;

                    // Set accessor
                    if (!child.IsConst())
                    {
                        tw.WriteLine("static duk_ret_t " + className + "_Set_" + child.name + DukSignature());
                        tw.WriteLine("{");
                        tw.WriteLine(Indent(1) + GenerateGetThis(classSymbol));
                        tw.WriteLine(Indent(1) + GenerateGetFromStack(child, 0, child.name));
                        tw.WriteLine(Indent(1) + "thisObj->" + child.name + " = " + child.name + ";");
                        tw.WriteLine(Indent(1) + "return 0;");
                        tw.WriteLine("}");
                        tw.WriteLine("");
                        newProperty.readOnly = false;
                    }

                    // Get accessor
                    {
                        tw.WriteLine("static duk_ret_t " + className + "_Get_" + child.name + DukSignature());
                        tw.WriteLine("{");
                        tw.WriteLine(Indent(1) + GenerateGetThis(classSymbol));
                        tw.WriteLine(Indent(1) + GeneratePushToStack(child, "thisObj->" + child.name));
                        tw.WriteLine(Indent(1) + "return 1;");
                        tw.WriteLine("}");
                        tw.WriteLine("");
                    }

                    properties.Add(newProperty);
                }
            }
        }

        static void GenerateMemberFunctions(Symbol classSymbol, TextWriter tw, Dictionary<string, List<Overload> > overloads, bool generateStatic)
        {
            string className = StripNamespace(classSymbol.name);

            foreach (Symbol child in classSymbol.children)
            {
                if (child.isStatic == generateStatic && child.kind == "function" && !child.name.Contains("operator") && child.visibilityLevel == VisibilityLevel.Public)
                {
                    if (!IsScriptable(child))
                        continue;

                    // \hack Unimplemented MathGeolib functions. Remove these checks when fixed
                    if (className == "float4" && child.name == "Orthogonalize")
                        continue;
                    if (className == "Plane" && child.name == "Distance" && child.parameters.Count == 1 && child.parameters[0].BasicType().Contains("float4"))
                        continue;

                    bool isClassCtor = !child.isStatic && (child.name == className);
                    if (!isClassCtor && !IsSupportedType(child.type))
                        continue;
                    // Bindings convention: refcounted objects like Scene or Component can not be constructed from script, but rather must be acquired from the framework
                    if (isClassCtor && classSymbol.FindChildByName("Refs") != null && classSymbol.FindChildByName("WeakRefs") != null)
                        continue;

                    bool badParameters = false;
                    for (int i = 0; i < child.parameters.Count; ++i)
                    {
                        if (!IsSupportedType(child.parameters[i].BasicType()))
                        {
                            badParameters = true;
                            break;
                        }
                        // For now skip all pointers. Will be needed later when exposing scene & components
                        if (child.parameters[i].BasicType().Contains('*'))
                        {
                            badParameters = true;
                            break;
                        }
                    }
                    if (badParameters)
                        continue;

                    string baseFunctionName = "";
                    if (!isClassCtor)
                        baseFunctionName = className + "_" + child.name;
                    else
                        baseFunctionName = className + "_Ctor";
                    if (child.isStatic)
                        baseFunctionName += "_Static";

                    // First overload?
                    if (!overloads.ContainsKey(baseFunctionName))
                        overloads[baseFunctionName] = new List<Overload>();

                    // Differentiate function name by parameters
                    string functionName = baseFunctionName;
                    for (int i = 0; i < child.parameters.Count; ++i)
                        functionName += "_" + SanitateTypeName(child.parameters[i].BasicType()).Replace(':', '_');
     
                    // Skip if same overload (typically a const variation) already included
                    bool hasSame = false;
                    foreach (Overload o in overloads[baseFunctionName])
                    {
                        if (o.functionName == functionName)
                        {
                            hasSame = true;
                            break;
                        }
                    }
                    if (hasSame)
                        continue;

                    Overload newOverload = new Overload();
                    newOverload.functionName = functionName;
                    newOverload.function = child;
                    newOverload.parameters = child.parameters;
                    overloads[baseFunctionName].Add(newOverload);

                    if (isClassCtor)
                    {
                        tw.WriteLine("static duk_ret_t " + functionName + DukSignature());
                        tw.WriteLine("{");

                        // \todo Remove unusable arguments, such as pointers
                        string args = "";
                        for (int i = 0; i < child.parameters.Count; ++i)
                        {
                            tw.WriteLine(Indent(1) + GenerateGetFromStack(child.parameters[i].BasicType(), i, child.parameters[i].name, child.parameters[i].IsAReference())); 
                            if (i > 0)
                                args += ", ";
                            if (NeedDereference(child.parameters[i]))
                                args += "*";
                                  
                            args += child.parameters[i].name;
                        }
                        tw.WriteLine(Indent(1) + className + "* newObj = new " + className + "(" + args + ");");
                        tw.WriteLine(Indent(1) + GeneratePushConstructorResultToStack(className, "newObj"));
                        tw.WriteLine(Indent(1) + "return 0;");
                        tw.WriteLine("}");
                        tw.WriteLine("");   
                    }          
                    else
                    {
                        tw.WriteLine("static duk_ret_t " + functionName + DukSignature());
                        tw.WriteLine("{");
                        string callPrefix = "";
                        if (!child.isStatic)
                        {
                            callPrefix = "thisObj->";
                            tw.WriteLine(Indent(1) + GenerateGetThis(classSymbol));
                        }
                        else
                            callPrefix = className + "::";
                        
                        string args = "";
                        for (int i = 0; i < child.parameters.Count; ++i)
                        {
                            tw.WriteLine(Indent(1) + GenerateGetFromStack(child.parameters[i].BasicType(), i, child.parameters[i].name, child.parameters[i].IsAReference()));
                            if (i > 0)
                                args += ", ";
                            if (NeedDereference(child.parameters[i]))
                                args += "*";

                            args += child.parameters[i].name;
                        }
                        if (child.type == "void")
                        {
                            tw.WriteLine(Indent(1) + callPrefix + child.name + "(" + args + ");");
                            tw.WriteLine(Indent(1) + "return 0;");
                        }
                        else
                        {
                            tw.WriteLine(Indent(1) + child.type + " ret = " + callPrefix + child.name + "(" + args + ");");
                            tw.WriteLine(Indent(1) + GeneratePushToStack(child.type, "ret"));
                            tw.WriteLine(Indent(1) + "return 1;");
                        }
                        tw.WriteLine("}");
                        tw.WriteLine(""); 
                    }
                }
            }
        }

        static void GenerateFunctionSelectors(Symbol classSymbol, TextWriter tw, Dictionary<string, List<Overload> > overloads)
        {
            foreach (KeyValuePair<string, List<Overload> > kvp in overloads)
            {
                if (kvp.Value.Count >= 2)
                {
                    tw.WriteLine("static duk_ret_t " + kvp.Key + "_Selector" + DukSignature());
                    tw.WriteLine("{");
                    tw.WriteLine(Indent(1) + "int numArgs = duk_get_top(ctx);");
                    foreach (Overload o in kvp.Value)
                    {
                        string argCheck = "if (numArgs == " + o.parameters.Count;
                        for (int i = 0; i < o.parameters.Count; ++i)
                            argCheck += " && " + GenerateArgCheck(o.parameters[i], i);
                        argCheck += ")";
                        tw.WriteLine(Indent(1) + argCheck);
                        tw.WriteLine(Indent(2) + "return " + o.functionName + "(ctx);");
                    }
                    tw.WriteLine(Indent(1) + "duk_error(ctx, DUK_ERR_ERROR, \"Could not select function overload\");");
                    tw.WriteLine("}");
                    tw.WriteLine("");   
                }
            }
        }

        static void GenerateFunctionList(Symbol classSymbol, TextWriter tw, Dictionary<string, List<Overload> > overloads, bool generateStatic)
        {
            string className = StripNamespace(classSymbol.name);

            if (overloads.Count == 0)
                return;
            if (!generateStatic)
                tw.WriteLine("static const duk_function_list_entry " + className + "_Functions[] = {");
            else
                tw.WriteLine("static const duk_function_list_entry " + className + "_StaticFunctions[] = {");
            bool first = true;
            foreach (KeyValuePair<string, List<Overload> > kvp in overloads)
            {
                if (kvp.Value[0].functionName.Contains("Ctor"))
                    continue;

                string prefix = first ? "" : ",";

                if (kvp.Value.Count >= 2)    
                    tw.WriteLine(Indent(1) + prefix + "{\"" + kvp.Value[0].function.name + "\", " + kvp.Key + "_Selector, DUK_VARARGS}");
                else
                    tw.WriteLine(Indent(1) + prefix + "{\"" + kvp.Value[0].function.name + "\", " + kvp.Value[0].functionName + ", " + kvp.Value[0].parameters.Count + "}");

                first = false;
            }

            tw.WriteLine(Indent(1) + ",{nullptr, nullptr, 0}");
            tw.WriteLine("};");
            tw.WriteLine("");  
        }

        static void GenerateExposeFunction(Symbol classSymbol, TextWriter tw, Dictionary<string, List<Overload> > overloads, Dictionary<string, List<Overload> > staticOverloads, List<Property> properties)
        {
            string className = StripNamespace(classSymbol.name);

            tw.WriteLine("void Expose_" + className + DukSignature());
            tw.WriteLine("{");
            
            bool hasCtor = false;
            string ctorName = className + "_Ctor";
            if (overloads.ContainsKey(ctorName))
            {
                hasCtor = true;
                if (overloads[ctorName].Count >= 2)
                    ctorName += "_Selector";
            }

            if (hasCtor)
                tw.WriteLine(Indent(1) + "duk_push_c_function(ctx, " + ctorName + ", DUK_VARARGS);");
            else
                tw.WriteLine(Indent(1) + "duk_push_object(ctx);");

            if (staticOverloads.Count > 0)
                tw.WriteLine(Indent(1) + "duk_put_function_list(ctx, -1, " + classSymbol.name + "_StaticFunctions);");
            tw.WriteLine(Indent(1) + "duk_push_object(ctx);");
            tw.WriteLine(Indent(1) + "duk_put_function_list(ctx, -1, " + classSymbol.name + "_Functions);");
            foreach (Property p in properties)
            {
                if (!p.readOnly)
                    tw.WriteLine(Indent(1) + "DefineProperty(ctx, \"" + p.name + "\", " + classSymbol.name + "_Get_" + p.name + ", " + classSymbol.name + "_Set_" + p.name + ");");
                else
                    tw.WriteLine(Indent(1) + "DefineProperty(ctx, \"" + p.name + "\", " + classSymbol.name + "_Get_" + p.name + ", nullptr);");
            }
            tw.WriteLine(Indent(1) + "duk_put_prop_string(ctx, -2, \"prototype\");");
            tw.WriteLine(Indent(1) + "duk_put_global_string(ctx, " + ClassIdentifier(classSymbol.name) + ");");
            tw.WriteLine("}");
            tw.WriteLine("");
        }

        static HashSet<string> FindDependencies(Symbol classSymbol)
        {
            HashSet<string> dependencies = new HashSet<string>();
            foreach (Symbol child in classSymbol.children)
            { 
                if (!IsScriptable(child))
                    continue;

                if (child.kind == "function" && !child.name.Contains("operator"))
                {
                    AddDependencyIfValid(classSymbol, dependencies, child.type); // Return type
                    foreach (Parameter p in child.parameters)
                        AddDependencyIfValid(classSymbol, dependencies, p.BasicType());
                }
            }

            return dependencies;
        }

        static string DukSignature()
        {
            return "(duk_context* ctx)";
        }

        static string ClassIdentifier(string className)
        {
            return className + "_Id";
        }

        static string SanitateTypeName(string type)
        {
            string t = type.Trim();
            if (t.EndsWith("&") || t.EndsWith("*"))
            {
                t = t.Substring(0, t.Length - 1).Trim();
                if (t.StartsWith("const"))
                    t = t.Substring(5).Trim();
            }
            if (t.EndsWith("const"))
                t = t.Substring(0, t.Length - 5).Trim();
            return StripNamespace(t);
        }

        static void AddDependencyIfValid(Symbol classSymbol, HashSet<string> dependencyNames, string typeName)
        {
            string t = SanitateTypeName(typeName);
            if (classSymbol.name == t)
                return; // Do not add self as dependency
                
            if (!Symbol.IsPODType(t) && classNames.Contains(t))
                dependencyNames.Add(t);
        }

        static bool IsSupportedType(string typeName)
        {
            string t = SanitateTypeName(typeName);
            return t == "void" || Symbol.IsPODType(t) || classNames.Contains(t) || t == "string" || t == "String";
        }

        static bool IsBadType(string type)
        {
            string t = SanitateTypeName(type);
            if (t == "string" || t == "String")
                return false;
            return type.Contains("bool *") || type.EndsWith("float *") || type.EndsWith("float3 *") || type.Contains("std::") || type.Contains("char*") || type.Contains("char *") || type.Contains("[");
        }

        static bool NeedDereference(Parameter p)
        {
            if (p.IsAPointer())
                return false;
            if (Symbol.IsPODType(p.BasicType()))
                return false;
            if (p.BasicType().Contains("string") || p.BasicType().Contains("String"))
                return false;

            return true;
        }

        static public string Indent(int num)
        {
            string s = "";
            int indentSize = 4;
            for (int i = 0; i < num * indentSize; ++i)
                s += " ";
            return s;
        }

        static bool IsScriptable(Symbol s)
        {
            if (s.argList.Contains("["))
                return false;
            if (IsBadType(s.type))
                return false;
            foreach (Parameter p in s.parameters)
                if (IsBadType(p.type) || IsBadType(p.BasicType()))
                    return false;

            foreach (string str in s.Comments())
                if (str.Contains("[noscript]"))
                    return false;
            if (s.returnComment != null && s.returnComment.Contains("[noscript]"))
                return false;
            return true;
        }
    }
}