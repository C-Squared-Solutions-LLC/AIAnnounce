using AIAnnounce;

static class FinishChecks
{
    public static void Run(Action<bool, string> check)
    {
        DateTime t0 = new(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        SessionView S(SessionState state, DateTime since, int pid = 1, string id = "a") => new()
        {
            Pid = pid, ProcStart = 7, SessionId = id, Title = "Docs site", Project = "docs-site", Cwd = @"C:\src\docs-site",
            State = state, Since = since,
        };

        var watch = new FinishWatch();
        check(watch.Update(new[] { S(SessionState.Busy, t0) }, t0.AddSeconds(1)).Count == 0, "A working session isn't finished");
        check(watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(40)) }, t0.AddSeconds(41)).Count == 0, "Going idle waits to settle");
        var done = watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(40)) }, t0.AddSeconds(45));
        check(done.Count == 1 && done[0].Worked == TimeSpan.FromSeconds(41), "A settled idle after real work is one finish, timed from when work began");
        check(watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(40)) }, t0.AddSeconds(60)).Count == 0, "A finish is announced once");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Busy, t0) }, t0.AddSeconds(1));
        watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(30)) }, t0.AddSeconds(30));
        watch.Update(new[] { S(SessionState.Busy, t0.AddSeconds(31)) }, t0.AddSeconds(31));
        check(watch.Update(new[] { S(SessionState.Busy, t0.AddSeconds(31)) }, t0.AddSeconds(40)).Count == 0, "Dipping idle between steps isn't a finish");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Busy, t0) }, t0);
        watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(2)) }, t0.AddSeconds(2));
        check(watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(2)) }, t0.AddSeconds(10)).Count == 0, "A two-second wake-up isn't worth announcing");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Busy, t0) }, t0);
        watch.Update(new[] { S(SessionState.Asked, t0.AddSeconds(30)) }, t0.AddSeconds(30));
        check(watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(60)) }, t0.AddSeconds(70)).Count == 0, "A turn that ended on a question gets the needs-you alert only");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Busy, t0) }, t0);
        watch.Update(new[] { S(SessionState.Waiting, t0.AddSeconds(20)) }, t0.AddSeconds(20));
        watch.Update(new[] { S(SessionState.Busy, t0.AddSeconds(50)) }, t0.AddSeconds(50));
        watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(80)) }, t0.AddSeconds(80));
        done = watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(80)) }, t0.AddSeconds(84));
        check(done.Count == 1 && done[0].Worked == TimeSpan.FromSeconds(80), "An approval mid-turn keeps the turn going and its clock running");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Idle, t0) }, t0);
        check(watch.Update(new[] { S(SessionState.Idle, t0) }, t0.AddMinutes(5)).Count == 0, "A session already idle at startup has nothing to announce");

        watch = new FinishWatch();
        watch.Update(new[] { S(SessionState.Busy, t0) }, t0);
        watch.Update(Array.Empty<SessionView>(), t0.AddSeconds(30));
        check(watch.Update(new[] { S(SessionState.Idle, t0.AddSeconds(40)) }, t0.AddSeconds(50)).Count == 0, "A session that went away and came back idle isn't a finish");

        byte[] wave = Chime.Wave();
        short Sample(int i) => BitConverter.ToInt16(wave, 44 + 2 * i);
        int count = (wave.Length - 44) / 2;
        int peak = Enumerable.Range(0, count).Max(i => Math.Abs((int)Sample(i)));
        check(wave.Length > 44 && System.Text.Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt "
              && BitConverter.ToInt32(wave, 24) == 44100 && BitConverter.ToInt32(wave, 40) == wave.Length - 44,
            "The finish chime is a well-formed 44.1 kHz PCM wave");
        check(peak > 10000 && peak < short.MaxValue * 0.5 && Math.Abs((int)Sample(count - 1)) < 50, "The finish chime is audible, below full scale, and ends without a click");
        check(TrayBalloon.CanSilence, "WinForms still exposes what a silent finish toast needs");
    }
}
