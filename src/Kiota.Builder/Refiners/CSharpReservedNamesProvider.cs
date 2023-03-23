﻿using System;
using System.Collections.Generic;

namespace Kiota.Builder.Refiners;
public class CSharpReservedNamesProvider : IReservedNamesProvider
{
    private readonly Lazy<HashSet<string>> _reservedNames = new(static () => new(105, StringComparer.OrdinalIgnoreCase) {
        "abstract",
        "add",
        "alias",
        "and",
        "as",
        "ascending",
        "async",
        "await",
        "base",
        "bool",
        "break",
        "by",
        "byte",
        "case",
        "catch",
        "char",
        "checked",
        "class",
        "const",
        "continue",
        "decimal",
        "default",
        "delegate",
        "descending",
        "do",
        "double",
        "dynamic",
        "else",
        "enum",
        "equals",
        "event",
        "explicit",
        "extern",
        "false",
        "finally",
        "fixed",
        "float",
        "for",
        "foreach",
        "global",
        "goto",
        "if",
        "implicit",
        "in",
        "init",
        "int",
        "interface",
        "internal",
        "is",
        "lock",
        "long",
        "managed",
        "nameof",
        "namespace",
        "new",
        "nint",
        "not",
        "notnull",
        "nuint",
        "null",
        "object",
        "on",
        "operator",
        "or",
        "out",
        "override",
        "params",
        "partial",
        "private",
        "protected",
        "public",
        "readonly",
        "record",
        "ref",
        "remove",
        "return",
        "sbyte",
        "sealed",
        "short",
        "sizeof",
        "stackalloc",
        "static",
        "string",
        "struct",
        "switch",
        "this",
        "throw",
        "true",
        "try",
        "typeof",
        "uint",
        "ulong",
        "unchecked",
        "unmanaged",
        "unsafe",
        "ushort",
        "using",
        "var",
        "virtual",
        "void",
        "volatile",
        "when",
        "while",
        "with",
        "yield",
    });
    public HashSet<string> ReservedNames => _reservedNames.Value;
}
