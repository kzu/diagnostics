// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DotNetConfig;
using Microsoft.Internal.Common.Commands;
using Microsoft.Tools.Common;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Counters
{
    public enum CountersExportFormat { csv, json };

    internal class Program
    {
        delegate Task<int> ExportDelegate(CancellationToken ct, List<string> counter_list, IConsole console, int processId, int refreshInterval, CountersExportFormat format, string output, string processName);

        private static Config Configuration { get; } = Config.Build();

        private static Command MonitorCommand() =>
            new Command(
                name: "monitor",
                description: "Start monitoring a .NET application")
            {
                // Handler
                CommandHandler.Create<CancellationToken, List<string>, IConsole, int, int, string>(new CounterMonitor().Monitor),
                // Arguments and Options
                CounterList(), ProcessIdOption(), RefreshIntervalOption(), NameOption()
            };

        private static Command CollectCommand() =>
            new Command(
                name: "collect",
                description: "Monitor counters in a .NET application and export the result into a file")
            {
                // Handler
                HandlerDescriptor.FromDelegate((ExportDelegate)new CounterMonitor().Collect).GetCommandHandler(),
                // Arguments and Options
                CounterList(), ProcessIdOption(), RefreshIntervalOption(), ExportFormatOption(), ExportFileNameOption(), NameOption()
            };

        private static Option NameOption() =>
            new Option(
                aliases: new[] { "-n", "--name" },
                description: "The name of the process that will be monitored.")
            {
                Argument = new Argument<string>(name: "name", getDefaultValue: () => Configuration.GetString("counters", "name"))
            };

        private static Option ProcessIdOption() =>
            new Option(
                aliases: new[] { "-p", "--process-id" },
                description: "The process id that will be monitored.")
            {
                Argument = new Argument<int>(name: "pid")
            };

        private static Option RefreshIntervalOption() =>
            new Option(
                alias: "--refresh-interval",
                description: "The number of seconds to delay between updating the displayed counters.")
            {
                Argument = new Argument<int>(name: "refresh-interval", getDefaultValue: () =>
                    Configuration.TryGetNumber("counters", "refresh-interval", out var interval) ? (int)interval : 1)
            };

        private static Option ExportFormatOption() =>
            new Option(
                alias: "--format",
                description: "The format of exported counter data.")
            {
                Argument = new Argument<CountersExportFormat>(name: "format", getDefaultValue: () =>
                    Configuration.TryGetString("counters", "format", out var format) ?
                    Enum.Parse<CountersExportFormat>(format) :
                    CountersExportFormat.csv)
            };

        private static Option ExportFileNameOption() =>
            new Option(
                aliases: new[] { "-o", "--output" },
                description: "The output file name.")
            {
                Argument = new Argument<string>(name: "output", getDefaultValue: () =>
                   Configuration.GetString("counters", "output") ?? "counter")
            };

        private static Argument CounterList() =>
            new Argument<List<string>>(name: "counter_list", getDefaultValue: () =>
            new List<string>(GetConfiguredCounters()))
            {
                Description = @"A space separated list of counters. Counters can be specified provider_name[:counter_name]. If the provider_name is used without a qualifying counter_name then all counters will be shown. To discover provider and counter names, use the list command.",
                Arity = ArgumentArity.ZeroOrMore
            };

        private static Command ListCommand() =>
            new Command(
                name: "list",
                description: "Display a list of counter names and descriptions, grouped by provider.")
            {
                CommandHandler.Create<IConsole, string>(List),
                RuntimeVersionOption()
            };

        private static Option RuntimeVersionOption() =>
            new Option(
                aliases: new[] { "-r", "--runtime-version" },
                description: "Version of runtime. Supported runtime version: 3.0, 3.1, 5.0")
            {
                Argument = new Argument<string>(name: "runtimeVersion", getDefaultValue: () =>
                    Configuration.GetString("counters", "runtimeVersion") ?? "3.1")
            };

        private static readonly string[] s_SupportedRuntimeVersions = new[] { "3.0", "3.1", "5.0" };

        private static IEnumerable<string> GetConfiguredCounters()
        {
            HashSet<string> counters = null;

            // Unqualified counters can be added directly as 'include' entries on the main 'counters' section, such as:
            // [counters]
            //    include = System.Runtime
            //    include = Microsoft.AspNetCore.Hosting

            foreach (var counter in Configuration.GetAll("counters", "include")
                .Select(entry => entry.RawValue)
                .Where(value => !string.IsNullOrEmpty(value)))
            {
                (counters ??= new HashSet<string>()).Add(counter);
            }

            // Each counter gets a section and each metric gets its own variable. This makes it easy to 
            // comment out one or several in a single edit operation in a text editor by just commenting a block
            // Example:
            // [counters "System.Runtime"]
            //    cpu-usage
            //    working-set
            //    assembly-count
            //    exception-count

            foreach (var counter in Configuration.GetRegex("counters")
                .Where(x => x.Section == "counters" && x.Subsection != null)
                .GroupBy(x => x.Subsection))
            {
                var qualified = counter.Key + "[" + string.Join(',', counter.Select(e => e.Variable)) + "]";
                // Replace potentially unqualified provider with the qualified one we just built.
                counters.Remove(counter.Key);
                counters.Add(qualified);
            }

            return counters;
        }

        public static int List(IConsole console, string runtimeVersion)
        {
            if (!s_SupportedRuntimeVersions.Contains(runtimeVersion))
            {
                Console.WriteLine($"{runtimeVersion} is not a supported version string or a supported runtime version.");
                Console.WriteLine("Supported version strings: 3.0, 3.1, 5.0");
                return 0;
            }
            var profiles = KnownData.GetAllProviders(runtimeVersion);
            var maxNameLength = profiles.Max(p => p.Name.Length);
            Console.WriteLine($"Showing well-known counters for .NET (Core) version {runtimeVersion} only. Specific processes may support additional counters.");
            foreach (var profile in profiles)
            {
                var counters = profile.GetAllCounters();
                var maxCounterNameLength = counters.Max(c => c.Name.Length);
                Console.WriteLine($"{profile.Name.PadRight(maxNameLength)}");
                foreach (var counter in profile.Counters.Values)
                {
                    Console.WriteLine($"    {counter.Name.PadRight(maxCounterNameLength)} \t\t {counter.Description}");
                }
                Console.WriteLine("");
            }
            return 1;
        }

        private static Task<int> Main(string[] args)
        {
            var parser = new CommandLineBuilder()
                .AddCommand(MonitorCommand())
                .AddCommand(CollectCommand())
                .AddCommand(ListCommand())
                .AddCommand(ProcessStatusCommandHandler.ProcessStatusCommand("Lists the dotnet processes that can be monitored"))
                .UseDefaults()
                .Build();
            return parser.InvokeAsync(args);
        }
    }
}
