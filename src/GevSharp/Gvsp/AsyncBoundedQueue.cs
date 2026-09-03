namespace GevSharp.Gvsp;

/// <summary>
/// 용량이 고정된 FIFO. 생산자(수신 스레드)는 <see cref="TryEnqueue"/> 로 넣고 가득 차면 즉시 false 를 받는다(블로킹 없음).
/// 소비자는 <see cref="DequeueAsync"/> 로 기다린다. <see cref="Complete"/> 뒤에는 대기 중·이후의 모든 꺼내기가 그 예외로 끝난다.
/// 항목이 있을 때의 꺼내기는 할당 없이 완료된 ValueTask 를 돌려준다.
/// </summary>
internal sealed class AsyncBoundedQueue<T>
{
    /// <summary>대기 중인 소비자 하나. 취소 콜백이 큐에서 스스로를 빼도록 큐와 토큰을 함께 들고 있다.</summary>
    private sealed class Waiter
    {
        public readonly TaskCompletionSource<T> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly AsyncBoundedQueue<T> Owner;
        public readonly CancellationToken Token;
        public CancellationTokenRegistration Registration;

        public Waiter(AsyncBoundedQueue<T> owner, CancellationToken token)
        {
            Owner = owner;
            Token = token;
        }

        public void OnCancel()
        {
            Owner.RemoveWaiter(this);
            Tcs.TrySetCanceled(Token);
        }
    }

    private readonly object _lock = new();
    private readonly Queue<T> _items;
    private readonly List<Waiter> _waiters = new();
    private readonly int _capacity;
    private Exception? _completion;

    public AsyncBoundedQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 1.");
        _capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public bool IsCompleted
    {
        get { lock (_lock) return _completion is not null; }
    }

    /// <summary>항목을 넣는다. 기다리는 소비자가 있으면 큐를 거치지 않고 바로 건넨다. 가득 찼거나 완료된 큐면 false.</summary>
    public bool TryEnqueue(T item)
    {
        Waiter? target = null;
        lock (_lock)
        {
            if (_completion is not null) return false;

            while (_waiters.Count > 0)
            {
                var w = _waiters[0];
                _waiters.RemoveAt(0);
                // 취소가 먼저 끝난 대기자는 건너뛴다 — 항목을 잃지 않으려면 TrySetResult 의 결과를 봐야 한다.
                if (w.Tcs.TrySetResult(item))
                {
                    target = w;
                    break;
                }
            }

            if (target is null)
            {
                if (_items.Count >= _capacity) return false;
                _items.Enqueue(item);
                return true;
            }
        }

        // 등록 해제는 락 밖에서 — 취소 콜백이 락을 기다리는 중이면 안에서 Dispose 하다가 서로 막힌다.
        target.Registration.Dispose();
        return true;
    }

    /// <summary>항목이 있으면 꺼낸다. 비어 있고 완료된 큐면 완료 예외를 던진다.</summary>
    public bool TryDequeue(out T item)
    {
        lock (_lock)
        {
            if (_items.Count > 0)
            {
                item = _items.Dequeue();
                return true;
            }
            if (_completion is not null) throw _completion;
            item = default!;
            return false;
        }
    }

    /// <summary>
    /// 항목이 있으면 꺼낸다. 비어 있으면 false — 완료된 큐에서도 던지지 않는다.
    /// 정지 경로에서 남은 항목을 반납할 때 쓴다. 그 자리에서는 완료 예외가 곧 "더 반납할 것이 없다" 가 아니라
    /// "여기서 멈춰라" 가 되어, 큐에 남은 프레임의 버퍼가 영영 돌아오지 않는다.
    /// </summary>
    public bool TryDrain(out T item)
    {
        lock (_lock)
        {
            if (_items.Count > 0)
            {
                item = _items.Dequeue();
                return true;
            }
            item = default!;
            return false;
        }
    }

    /// <summary>항목이 올 때까지 기다린다. 완료된 큐면 완료 예외, 토큰 취소면 <see cref="OperationCanceledException"/>.</summary>
    public ValueTask<T> DequeueAsync(CancellationToken ct = default)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (_items.Count > 0) return new ValueTask<T>(_items.Dequeue());
            if (_completion is not null) return new ValueTask<T>(Task.FromException<T>(_completion));
            if (ct.IsCancellationRequested) return new ValueTask<T>(Task.FromCanceled<T>(ct));

            waiter = new Waiter(this, ct);
            _waiters.Add(waiter);
        }

        if (ct.CanBeCanceled)
        {
            waiter.Registration = ct.Register(static s => ((Waiter)s!).OnCancel(), waiter);
            // 등록 전에 항목이 건네졌으면 등록이 남는다 — 여기서 정리한다.
            if (waiter.Tcs.Task.IsCompleted) waiter.Registration.Dispose();
        }

        return new ValueTask<T>(waiter.Tcs.Task);
    }

    /// <summary>큐를 닫는다. 대기 중인 소비자는 모두 ex 로 실패하고, 이후의 꺼내기도 같은 예외를 받는다. 두 번째 호출은 무시된다.</summary>
    public void Complete(Exception ex)
    {
        if (ex is null) throw new ArgumentNullException(nameof(ex));

        Waiter[] pending;
        lock (_lock)
        {
            if (_completion is not null) return;
            _completion = ex;
            pending = _waiters.ToArray();
            _waiters.Clear();
        }

        foreach (var w in pending)
        {
            w.Tcs.TrySetException(ex);
            w.Registration.Dispose();
        }
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (_lock)
        {
            _waiters.Remove(waiter);
        }
    }
}
