// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NeoAstra.Tests")]
// The in-memory RPC benchmarks open sessions that send in batches, as a view binding does.
[assembly: InternalsVisibleTo("NeoAstra.Benchmarks")]
