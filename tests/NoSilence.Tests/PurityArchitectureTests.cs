using System.Reflection;
using NoSilence.Detection;

namespace NoSilence.Tests;

/// <summary>
/// Guards the boundary the whole design rests on.
/// </summary>
/// <remarks>
/// The decision engine is a pure function of (snapshot, settings, state) with no COM, no
/// Win32 and no clock. That is what lets a recorded session be replayed through it offline,
/// which is the only way to answer "is this threshold right?". The property is easy to break
/// by accident — one convenient <c>DateTime.Now</c> or one NAudio type leaking into a record
/// would do it — and impossible to notice until replay quietly stops matching reality.
/// </remarks>
public class PurityArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "NAudio",
        "System.Windows.Forms",
        "System.Drawing",
        "Serilog",
        "TagLibSharp",
    ];

    private static IEnumerable<Type> PureTypes => typeof(DecisionEngine).Assembly
        .GetTypes()
        .Where(t => t.Namespace?.StartsWith("NoSilence.Detection", StringComparison.Ordinal) == true);

    [Fact]
    public void TheDetectionNamespaceIsNotEmpty()
    {
        // A typo in the namespace filter would make every other test here pass vacuously.
        Assert.NotEmpty(PureTypes);
    }

    [Fact]
    public void NoDetectionTypeTouchesAudioOrUiLibraries()
    {
        var violations = new List<string>();

        foreach (Type type in PureTypes)
        {
            foreach (Type referenced in ReferencedTypes(type))
            {
                string? assembly = referenced.Assembly.GetName().Name;
                if (assembly is not null && ForbiddenAssemblies.Any(f => assembly.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{type.Name} references {referenced.Name} from {assembly}");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Distinct()));
    }

    [Fact]
    public void NoDetectionTypeUsesInterop()
    {
        var violations = new List<string>();

        foreach (Type type in PureTypes)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                {
                    violations.Add($"{type.Name}.{method.Name} is a P/Invoke");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Behavioural proof that the engine takes its time from the snapshot and not from a
    /// clock: the same sequence evaluated with timestamps twenty years apart must give
    /// identical decisions. Replay depends entirely on this.
    /// </summary>
    [Fact]
    public void TheEngineIgnoresTheWallClock()
    {
        var config = new DetectionConfig
        {
            PollIntervalMs = 250,
            MinDurationMs = 1000,
            ReleaseMs = 2000,
            HardDuckGraceMs = 0,
            MicrophoneSignal = false,
            FullscreenSignal = false,
        };

        List<bool> Evaluate(DateTimeOffset start)
        {
            var state = new DecisionState();
            var results = new List<bool>();
            DateTimeOffset clock = start;

            foreach (double level in Levels())
            {
                var snapshot = DetectionSnapshot.Empty(clock) with
                {
                    Render = [Session(level)],
                };

                results.Add(DecisionEngine.Evaluate(snapshot, config, state).WantsSilence);
                clock = clock.AddMilliseconds(config.PollIntervalMs);
            }

            return results;
        }

        List<bool> longAgo = Evaluate(new DateTimeOffset(2006, 1, 1, 0, 0, 0, TimeSpan.Zero));
        List<bool> farFuture = Evaluate(new DateTimeOffset(2046, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(longAgo, farFuture);
        Assert.Contains(true, longAgo);    // and it actually did something
        Assert.Contains(false, longAgo);
    }

    private static IEnumerable<double> Levels()
    {
        for (int i = 0; i < 20; i++)
        {
            yield return -100;
        }

        for (int i = 0; i < 20; i++)
        {
            yield return -12;
        }

        for (int i = 0; i < 40; i++)
        {
            yield return -100;
        }
    }

    private static SessionObservation Session(double dbfs) => new(
        "s1", "e1", "Headphones", 100, "vlc.exe", null, false, false,
        SessionActivity.Active, (float)PeakMath.FromDbfs(dbfs), 1f, false);

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (Type iface in type.GetInterfaces())
        {
            yield return iface;
        }

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in type.GetFields(All))
        {
            foreach (Type t in Unwrap(field.FieldType))
            {
                yield return t;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(All))
        {
            foreach (Type t in Unwrap(property.PropertyType))
            {
                yield return t;
            }
        }

        foreach (MethodInfo method in type.GetMethods(All))
        {
            foreach (Type t in Unwrap(method.ReturnType))
            {
                yield return t;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type t in Unwrap(parameter.ParameterType))
                {
                    yield return t;
                }
            }
        }
    }

    /// <summary>Yields a type and, for generics, its arguments — a List&lt;NAudioThing&gt; counts.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            foreach (Type inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }
}
