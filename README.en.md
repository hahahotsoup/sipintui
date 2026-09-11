# 🍲 sip

> **English** | [**简体中文**](./README.md)

> ——"Savor it, sip it slow."

> 📖 This development phase is wrapping up for now. If you'd like to know how this software came to be, read: [《读不进去书的人，做了一个读书的软件》](https://blog.hotsouprealm.top/读不进去书的人，做了一个读书的软件/)

> 🧭 **Roadmap: TUI will be phased out.** The terminal UI still works for now, but new work and long-term maintenance focus on **CLI + Web**. Bare `sip` will not grow more interactive features; prefer the command line and [sip-web](https://github.com/hahahotsoup/sip-webapiextra). TUI may remain through a few stable releases and then be removed — watch Release Notes for the cutoff.

Welcome~

No matter how you found this — an AI recommendation, some forum thread, or a friend casually dropping the link — thank you for clicking in, and I hope you'll stay on this page for five minutes.

---

## Does any of this sound familiar?

The blogs you follow update, but you can't be bothered to open each one.

An author quietly flips a conclusion, and you never know — out in the wild, that's how facts get twisted.

You subscribe to a dozen feeds, get a hundred posts a day, can't read them, so you just "mark all read."

You ask an AI to check something, and it cites a pile of sources you don't trust at all.

The sources look impressive, but you just can't get into them.

You want to cross-check from many angles, but you're drowning in sheer volume.

Your RSS reader just feels a bit clunky to you.

If any of these ring a bell, sip can probably help.

## Give it a try?

sip is a **local-first personal information hub**: download one exe, run it, add a few sources, and come back tomorrow.

Single-file builds for Windows / macOS / Linux. No sign-up, no cloud — everything lives on your machine in a folder called `readwithhotsoup`. Copy it and you've migrated.

Once it's open, just feel your way around.

> 🌐 **Don't want to touch the terminal? Try sip-web** — one command starts a zero-dependency **local web UI**: subscribe, read, and search right in your browser, no command line needed. [Take a look →](https://github.com/hahahotsoup/sip-webapiextra)

Take it slow, no rush. It's a cure for information overload.

## What it can do for you

sip revolves around five verbs, each backed by real features with concrete value:

- **Collect** — add a few RSS sources, or import from OPML in one go. All-local, no sign-up, no cloud — you pick the sources.
- **Preserve** — fetch the full text when a summary is too short (`--fulltext`); versions are kept automatically; reading progress remembers where you stopped, so you can pick it back up.
- **Track** — the author quietly flips a conclusion or a number? `sip --diff` shows you the before and after in one screen; silently-edited facts can't hide.
- **Filter** — five or six sources repost the same piece? It clusters them so you don't re-read; each day a small bowl of sip today, not greedy; Source Policy helps you lower the frequency of or archive sources you don't read.
- **Use** — full-text search (`--grep`) needs no AI; semantic search (`--search`) finds "similar in meaning"; export Markdown; let an agent answer only from sources you trust.

At the end of the day you get three things: **information overload tamed, silently-edited facts exposed, and AI that no longer cites sources you don't trust.**

## What if I really can't figure it out?

Honestly, I designed for that from the start: sip isn't just for humans — it's for agents too.

We built **agent-invocation capability** — any agent can call sip through the CLI (with a skill), with capability nearly identical to the TUI (except `init`, which touches your API key). People and AI are first-class citizens.

## What's different from other RSS readers

FreshRSS, Feedly, Inoreader are all fine — when it comes to aggregating subscriptions and showing articles, they do it well, and sip isn't trying to compete with them on that.

What sip wants to do is what they generally don't and won't: **make information trustworthy, traceable, and usable by AI.**

- The author edits a post or flips a conclusion? `--diff` leaves you the trace of change, instead of you forever seeing the old version.
- Want AI to look things up but worry it cites junk? AI only searches the sources you subscribed to.
- Information overload keeping you scrolling? A small bowl of sip today each day, then close it.

That's why sip is a single local file: when an author edits a post it quietly keeps a version, five sources reposting the same piece get clustered, and a report lays the facts out for you. Under the hood, just two things — **deterministic rules**, and **local storage of facts**. The judgment is yours; AI is just a quiet assistant helping you understand, never deciding the value for you.

A fuller side-by-side is on the [Wiki · Competitor comparison](https://sip.hotsouprealm.top/了解/竞品对比.html).

AI can help you understand information, but it never decides its value for you — it's all up to you.

## A little product philosophy

Honestly, the world isn't short of "smarter" readers. sip isn't trying to be smarter than them — it's trying to hand the judgment back to you.

Like the line from *Let the Bullets Fly* — "I want to stand tall AND make the money." sip is a bit the same: **stand tall, and still read what you care about.** No kowtowing to algorithms, no going with the herd — and you still get to read the things that matter to you.

Information itself is neither good nor bad; whether something is "worth reading" is for you to say. AI can dig up the facts and point out the changes, but "does this source still matter to me" — that's your call.

That's why sip sticks to two things: **deterministic rules**, and **local storage of facts**. You set the rules, the facts stay clean, and it doesn't step past that line.

Which is also why telemetry is off by default and AI just quietly watches — it's not here to live your life for you.

## A few little things inside

🍵 **sip today**: a small bowl of worthwhile reads each day — five, or however many you set. Not greedy, no rush.

💧 **Sumenia**: a cute girl who's absent by default; only after you invite her does she quietly note your reading habits, locally. Don't invite her and she doesn't exist.

📜 **Version tracking**: author changes a claim or a number, you see the diff — almost like git.

🔒 **Simon (孟思琳)**: the always-on security guardian — DB self-heal, SSRF/terminal-injection protection, non-interactive call control. On by default, cannot be disabled, level only.

## As for how to use it

Besides clicking in the UI, the CLI works too:

    sip --today              # what to read today
    sip --search "RAG"       # semantic search (run sip --init to configure AI first)
    sip --grep "quantum"     # full-text search, no AI needed
    sip --diff 12 v1 v3      # see what changed from v1 to v3
    sip --insights           # a reading report; the facts are yours, the call is yours

Oh, and `sip --init` asks for your API key, so run it yourself in a real terminal — scripts can't do it.

## Want AI to answer only from sources you trust?

Wire sip into a group or bot with OpenClaw or Cherry Studio — it answers only from your subscribed sources. Junk citations, goodbye. Setup steps: [Wiki · Bot Integration](https://sip.hotsouprealm.top/使用/Bot.html).

## About security

sip is conservative by nature, because it believes information is yours first:

- All data stays local — no sign-up, no cloud, no upload. Copy `readwithhotsoup` and you've migrated.
- Telemetry is off by default; only after you turn it on does it record locally, never uploading; clear/export anytime.
- Full-text fetching has SSRF protection: http/https only; internal/loopback/cloud-metadata addresses rejected.
- The database has integrity checks and WAL — it self-heals after crashes, no data loss.
- `--init` must be run manually in a real terminal — API keys never enter scripts or logs.
- Sensitive records (search terms) stay local; clear with `telemetry export/clear` anytime.
- **Simon (孟思琳) security guardian**: DB self-heal, SSRF/terminal-injection protection — on by default, cannot be disabled, level only (`sip simon status`). Level 2 rejects all CLI writes; level 3 rejects all CLI calls (only `simon status` remains; everything goes through the TUI, and downgrades only in the TUI command bar). Level 3 also encrypts all data (SQLCipher + AES) with an auto-generated key in the OS credential store (scoped per data directory, so multiple copies don't interfere) — other software can't read your data; migrate machines with `sip simon export-key`.

In one sentence: it doesn't collect, track, or secretly upload your stuff.

## Recently

- 🧹🌳 **v1.2.2 "Data Health + Tree Comments + Multi-tag"**:
  - `sip ingest stats` one-line summary (total evidence, versions, modified, reversed, topics, tags, new today)
  - `sip ingest cleanup --stale` clean stale evidence (keeps items with ViewCount ≥ 3 or recently viewed)
  - `sip ingest tree` tree-structured comments (FragmentId + recursive CTE)
  - `sip ingest tag` multi-tag management (Tags + EvidenceTags many-to-many)
  - `sip ingest watch` web monitoring — mark evidence for **manual refresh** (no auto-fetch)
  - `sip --diff --semantic` semantic diff — shows semantic distance and change grade (⚪polish/🟡adjust/🔴reverse)
- ⚡ **Million-scale adaptation**: on a 1M-article library — `--grep` full-text search 2.2s → 0.5s (FTS5, Chinese substring searchable), the TUI opens instantly (lazy sidebar), `--today` 7.5s → 3s, whole-feed updates in one transaction.
- 🧪 **Automated test baseline**: 97 process-level black-box cases + GitHub Actions CI — automatic regression on every change.
- 🔒 **Simon (孟思琳)**: the always-on guardian (see Security above) — level 3 encrypts everything; keys are auto-generated in the OS credential store; you never have to remember any key.

## More

Full docs at [sip.hotsouprealm.top](https://sip.hotsouprealm.top/); to build from source, see the [Wiki](https://sip.hotsouprealm.top/上手/快速开始.html).

Also, shameless plug.

Follow the hot soup teahouse, follow the hot soup teahouse, thank you: [https://blog.hotsouprealm.top/](https://blog.hotsouprealm.top/atom.xml)

Check out our self-test report: [https://sip.hotsouprealm.top/测试报告.html](https://sip.hotsouprealm.top/%E6%B5%8B%E8%AF%95%E6%8A%A5%E5%91%8A.html)

---

When you open sip, you know what you read today is trustworthy; when your AI calls sip, you know the sources it cites are reliable.

May we meet again, none the worse for wear 🍲

Licensed under the GNU General Public License v3.0 (GPL-3.0)
