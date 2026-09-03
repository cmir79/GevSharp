using GevSharp.Cli.Commands;

// 진입점은 얇게 — Ctrl+C 를 취소 토큰으로 바꾸고, 파싱·분기·실행은 CliApp 이 맡는다. 종료 코드: 0 ok / 1 usage / 2 device / 3 stream.
using var cancel = new ConsoleCancel();
return await CliApp.RunAsync(args, cancel.Token);
