# Apache Calcite adapter for Azure Cosmos DB

`DESIGN.md` is where the reasoning lives and `TODO.md` is what is left; both are written to be read
before changing the thing they describe.

## Pull requests

**Check whether a pull request is stacked, every time, and mark it if it is.** A branch taken from
another open branch rather than from `main` is stacked, and GitHub shows nothing about that on its
own — the diff silently includes the parent's commits, so a reviewer reads changes that are not
under review and a merge to `main` takes work that was never approved.

Marking one is two things, and neither substitutes for the other:

- **Set the base branch to the parent**, not `main`. This is what makes the diff show only the work
  in front of the reviewer. It also means the pull request must be retargeted, or merged after its
  parent, or GitHub will close it when the parent merges.
- **Say so in the body, in the first line.** `Stacked on #N — base branch is <parent>`, with the
  reason the stack exists. A base branch is a field somebody has to go and look at; a sentence is
  read by whoever opens the page.

**Reference it from the parent too.** A stack is only visible from below otherwise, and the person
deciding whether to merge the parent is the one who most needs to know something sits on it. One
line at the top of the parent's body: `#M is stacked on this`.

**Check the parent is still open before relying on any of it.** A parent that merges while the child
is in review leaves the child's base branch deleted and its diff suddenly enormous; rebase onto
`main` and retarget rather than leaving it. This happened to #133 mid-session, and to
`feature/json-query-array-returning` before it.

**And check before opening, not after.** The question is whether the branch point is `main` —
`git merge-base --is-ancestor origin/main HEAD` says so, and `git log --oneline origin/main..HEAD`
shows what the diff will actually carry.
