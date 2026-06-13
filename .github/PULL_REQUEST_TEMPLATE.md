<!--
Thanks for contributing to Telegrab! Please read CONTRIBUTING.md before opening this PR.
Keep PRs small and single-purpose where possible.
-->

## Summary

<!-- What does this PR change, and why? -->

## Related issue

<!-- e.g. Closes #123. If there is no issue, briefly explain the motivation. -->

## Type of change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that changes existing behavior)
- [ ] Documentation only
- [ ] Refactor / internal change (no user-visible behavior change)

## How was this tested?

<!-- Describe the tests you ran and how to reproduce them. -->

- [ ] `dotnet build` is clean
- [ ] `dotnet test` passes
- [ ] Manually verified affected UI flows

## Checklist

- [ ] My code follows the conventions in CONTRIBUTING.md and `.kiro/steering/`.
- [ ] User-facing text is in English; code comments are in Indonesian.
- [ ] Pure-logic services (`ManifestDbService`, `DocumentationRenderer`, `CaptionResolver`, etc.)
      remain free of MAUI/WTelegram dependencies.
- [ ] I added/updated tests for new behavior (required for pure-logic and download/lifecycle changes).
- [ ] I did not commit secrets or private data (`appsettings.json`, `session.dat`, `telegrab.db`).
- [ ] I cleaned up temporary files created during testing.

## Notes for reviewers

<!-- Tradeoffs, follow-ups, or anything that needs special attention. -->
