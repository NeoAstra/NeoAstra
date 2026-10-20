// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NeoAstra")]
[assembly: InternalsVisibleTo("NeoAstra.Tests")]
[assembly: InternalsVisibleTo("NeoAstra.NativeAotFixture")]
[assembly: InternalsVisibleTo("NeoAstra.Tool")]
// The RPC benchmarks drive the transport of a view without a browser.
[assembly: InternalsVisibleTo("NeoAstra.Benchmarks")]
