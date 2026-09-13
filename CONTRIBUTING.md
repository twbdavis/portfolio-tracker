# Contributing

Bug reports, documentation improvements, and focused pull requests are welcome.
For a larger feature, open an issue first so its scope and intended behavior can be discussed.

## Development

Follow the prerequisites in the [README](README.md), then run:

```text
powershell -File scripts/setup.ps1 -NoLaunch
dotnet test PortfolioTracker.slnx
npm --prefix frontend test
```

Keep changes focused. Include a regression test for a bug fix and update relevant
documentation. For interface changes, include a screenshot using fictional data.
Explain the problem, resulting behavior, and checks performed in your pull request.
Never include credentials, personal records, generated build folders, or customer data.

## Reporting problems

Use the issue forms for reproducible bugs and feature requests. Include your app
version, operating system or browser, steps to reproduce, and expected versus actual
results. Check existing issues first. Share security concerns privately using
[SECURITY.md](SECURITY.md).

Be respectful, specific, and constructive. Contributions are provided under the
repository's [MIT license](LICENSE).
