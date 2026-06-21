# Is Prism safe? Yes. Here's the proof. 🔺

Short answer: **Prism does not steal your account, your cookies, or anything else.** Your stuff never leaves your PC except to talk to Roblox itself.

## Why your antivirus / friends freak out

Prism does three things that *look* sketchy to antivirus but are 100% normal for what it is:

1. **It handles Roblox login cookies** — because it has to, to launch your accounts. AV can't tell the difference between a safe account manager and a stealer, so it assumes the worst.
2. **It's not "signed"** — code-signing certificates cost hundreds of dollars a year. Lots of free, safe apps skip it. Windows then shows a scary "unknown publisher" popup. That popup does NOT mean it's a virus.
3. **It updates itself** — it downloads the newest version from its own GitHub page. AV sometimes flags any app that downloads an update.

None of these mean malware. They just trip automatic alarms.

## What Prism actually does with your account

- Your login cookie is **encrypted on your own PC** using Windows' built-in protection (DPAPI). If someone copied the file to another computer, it would be **useless** to them.
- The cookie is **only ever sent to roblox.com** — the code literally refuses to send it anywhere else. (Anyone can check this in the code.)
- **No "send my data to a hacker" server exists anywhere in the app.** There's no hidden Discord webhook, no secret upload, nothing.
- It does **not** ask for admin rights, doesn't install itself into Windows startup, and doesn't hide anything.

## Don't trust me — verify it yourself

Pick whichever you like:

- **Read the code.** It's all public on GitHub. The account/cookie stuff is in the `Services` folder. Every web address it talks to is roblox.com or its own GitHub page.
- **Build it yourself.** If you have .NET installed:
  ```
  dotnet publish -c Release -r win-x64
  ```
  You'll get the exact same app, built by your own machine.
- **Scan it.** Upload the `.exe` to [virustotal.com](https://www.virustotal.com). Any hits will be the generic "this looks like a Roblox tool" guesses I described above — not a real virus signature.

## TL;DR

It looks scary because it touches Roblox accounts and isn't signed — that's it. The code is open, it only talks to Roblox, your cookies are encrypted on your own machine, and nothing gets sent to anyone. You're good. 👍
