using System.Threading.Tasks.Sources;

namespace PlaytestAgent.Kit;

public sealed class WaitCounter
{
    public int Count;

    public CancellationToken Token;
}

// Drives one executor a tick at a time. What a Python generator gets from next(gen), an async method gets from this: the
// executor runs until it parks at Tick (or ends), reads included, and the driver awaits that boundary. The boundary's
// signal is armed before the executor is resumed, because the executor may park on another thread at any moment.
public sealed class Stepper : IValueTaskSource
{
    internal static readonly AsyncLocal<WaitCounter?> Counter = new();

    readonly Game _game;
    readonly Func<CancellationToken, Task<Result>> _run;
    readonly CancellationTokenSource _cts;
    readonly Boundary _boundary = new();
    readonly Action _ended;
    Task<Result>? _task;
    Action<object?>? _parked;
    object? _parkedState;

    public Stepper(Game game, Func<CancellationToken, Task<Result>> run, CancellationToken outer = default)
    {
        _game = game;
        _run = run;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer.CanBeCanceled ? outer : game.Ambient);
        Waits.Token = _cts.Token;
        _ended = _boundary.Signal;
    }

    public WaitCounter Waits { get; } = new();

    public bool Started => _task is not null;

    public bool Done => _task is { IsCompleted: true };

    public Task<Result>? Task => _task;

    public CancellationToken Token => _cts.Token;

    public ValueTask Tick() => new(this, 0);

    public ValueTask Step()
    {
        _game.Current = this;
        _boundary.Arm();
        if (_task is null)
            Start();
        else
            Resume();
        return new ValueTask(_boundary, _boundary.Version);
    }

    void Start()
    {
        _task = Guarded();
        if (_task.IsCompleted)
            _boundary.Signal();
        else
            _task.GetAwaiter().UnsafeOnCompleted(_ended);
    }

    async Task<Result> Guarded()
    {
        Counter.Value = Waits;
        return await _run(_cts.Token);
    }

    void Resume()
    {
        var continuation = _parked ?? throw new InvalidOperationException("the executor is not parked at a tick");
        var state = _parkedState;
        _parked = null;
        _parkedState = null;
        continuation(state);
    }

    // Cancels the token, then waits for the executor's finally blocks. Only then may the caller release input, or those
    // blocks would let go of what the next option has just pressed.
    public async ValueTask<bool> Stop(double patience = 2)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_task is null || _task.IsCompleted)
            return true;
        if (_parked is not null)
        {
            _boundary.Arm();
            Resume();
        }
        var finished = await System.Threading.Tasks.Task.WhenAny(_task,
            System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(patience))).ConfigureAwait(false);
        return finished == _task;
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) =>
        _cts.IsCancellationRequested ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Pending;

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        if (_parked is not null)
            throw new InvalidOperationException("two controllers ticked at once: await one, then the other");
        _parked = continuation;
        _parkedState = state;
        _boundary.Signal();
    }

    void IValueTaskSource.GetResult(short token) => _cts.Token.ThrowIfCancellationRequested();

    sealed class Boundary : IValueTaskSource
    {
        ManualResetValueTaskSourceCore<bool> _core;
        int _signalled;

        public short Version => _core.Version;

        public void Arm()
        {
            _core.Reset();
            Volatile.Write(ref _signalled, 0);
        }

        public void Signal()
        {
            if (Interlocked.Exchange(ref _signalled, 1) == 0)
                _core.SetResult(true);
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public void GetResult(short token) => _core.GetResult(token);
    }
}
