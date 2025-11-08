A server-side mod for Vintage Story that allows administrators to temporarily mute players from global chat, they still will be able to write in group chats tho. 


`/mute <player_name> <duration> [duration] [duration]`
Temporarily mute a player from global chat. Duration format: `1d`, `20h`, `45m` (days, hours, minutes) - can use 1 to 3 arguments in any order. Limits: max 365d, 24h, 60m per argument. Examples: `/mute PlayerName 1d`, `/mute PlayerName 20h 45m`, `/mute PlayerName 1d 2h 30m`

`/unmute <player_name>`
Remove the mute from a player, allowing them to chat again.


`/mutelist`
Display all currently muted players with their remaining mute time.


All ChatMute commands require the **`kick`** privilege. 
