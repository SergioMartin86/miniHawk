-- Play a project forward with TASTUDIO OPEN, unattended.
--
-- Every frontend leg in tests/synth drives Chimera with the piano roll CLOSED,
-- and a piano roll open is a different frame loop: every frame is captured into
-- the greenzone, a note is read back per frame, and the roll redraws against
-- what it finds. A fault that only happens with it open cannot be reproduced by
-- any existing leg - which is exactly the shape of the PS2 report this was
-- written for.
--
-- Environment (all optional):
--   CHIMERA_SOAK_FRAMES  frames to play, default 2000
--   CHIMERA_SOAK_EVERY   log a line every N frames, default 50
--   CHIMERA_SOAK_LOG     append progress here as well as to the console
--   CHIMERA_SOAK_SPEED   speedmode percentage, default 6400 (0 leaves it alone)
--
-- It says what it did on every line it writes, because the interesting run is
-- the one that DIES: the last line before the process disappears is the whole
-- result, so nothing is buffered until the end.

local frames = tonumber(os.getenv("CHIMERA_SOAK_FRAMES") or "") or 2000
local every  = tonumber(os.getenv("CHIMERA_SOAK_EVERY") or "") or 50
local speed  = tonumber(os.getenv("CHIMERA_SOAK_SPEED") or "") or 6400
local logPath = os.getenv("CHIMERA_SOAK_LOG")

local logFile = nil
if logPath then logFile = io.open(logPath, "a") end
local function say(line)
	console.log(line)
	if logFile then logFile:write(line .. "\n"); logFile:flush() end
end

say("soak: system=" .. tostring(emu.getsystemid()) .. " frame=" .. emu.framecount())

if not tastudio.engaged() then
	client.opentasstudio()
end
say("soak: tastudio engaged=" .. tostring(tastudio.engaged()))
if not tastudio.engaged() then
	say("soak: FAILED to open tastudio")
	client.exit()
end

if speed > 0 then pcall(function() client.speedmode(speed) end) end

-- TAStudio owns playback while it is engaged: it decides which frame the
-- emulator is on and drives the greenzone around it. Telling it where to go and
-- then pumping frames is what a person holding down Play does; calling
-- frameadvance at it without saying where to go leaves it paused, waiting for a
-- decision that never comes.
local start = emu.framecount()
say("soak: playing from " .. start)
if tastudio.engaged() then
	pcall(function() tastudio.setplayback(start + frames) end)
	pcall(function() client.unpause() end)
	say("soak: seek target " .. tostring(tastudio.getseekframe()))
end

for i = 1, frames do
	emu.frameadvance()
	if i == 1 then say("soak: first frameadvance returned, at " .. emu.framecount()) end
	if i % every == 0 then
		local f = emu.framecount()
		-- hasstate() is the greenzone answering, so this also proves the piano
		-- roll is really being fed rather than merely being on screen
		say(string.format("soak: frame %d (played %d) state=%s lag=%s",
			f, i, tostring(tastudio.hasstate(f)), tostring(tastudio.islag(f))))
	end
end

say("soak: SURVIVED " .. frames .. " frames, now at " .. emu.framecount() .. " (started " .. start .. ")")
if logFile then logFile:close() end
client.exit()
