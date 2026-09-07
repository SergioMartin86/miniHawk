using System.Collections.Generic;
using System.Diagnostics;

using Chimera.Common.StringExtensions;

namespace Chimera.Emulation.Common
{
	public static class MnemonicLookup
	{
		private const string PFX_RETROPAD = "RetroPad ";

		/// <remarks>arcade-only appendix to resolve name clashes</remarks>
		private const string MAME_TAG = " [:";

		public static char Lookup(string button, string systemId)
		{
			var key = button.Replace("Key ", "");
			if (key.StartsWith('P'))
			{
				if (key.Length > 2 && key[1] == '1' && key[2] >= '0' && key[2] <= '9') // Hack to support 10-20 controllers, TODO: regex this thing instead
				{
					key = key.Substring(4);
				}
				else if (key.Length > 1 && key[1] >= '0' && key[1] <= '9')
				{
					key = key.Substring(3);
				}
			}
			key = key.RemovePrefix(PFX_RETROPAD);

			if (key.Contains(MAME_TAG))
			{
				key = key[..key.IndexOf(MAME_TAG)];
			}

			if (SystemOverrides.TryGetValue(systemId, out var overridesForSys) && overridesForSys.TryGetValue(key, out var c))
			{
				return c;
			}

			if (BaseMnemonicLookupTable.TryGetValue(key, out var c1))
			{
				return c1;
			}

			if (key.Length == 1)
			{
				return key.StartsWith('.') ? 'p' : key[0];
			}

			// A name no table knows. '!' used to be the answer, which heads a
			// TAStudio column with a character that names nothing and tells a
			// person only that something is wrong somewhere. The last word is a
			// better guess than that - "Stick Fire" is an F, "FDS Insert 2" is a
			// 2 - and it is a guess: a controller whose columns must be told
			// apart belongs in the table above, and the mnemonic test says so.
			var lastWord = key.Substring(key.LastIndexOf(' ') + 1);
			return lastWord.Length is 0 ? '?' : lastWord[0];
		}

		public static string LookupAxis(string button, string systemId)
		{
			var key = button
				.Replace("P1 ", "")
				.Replace("P2 ", "")
				.Replace("P3 ", "")
				.Replace("P4 ", "")
				.Replace("Key ", "");

			if (AxisSystemOverrides.TryGetValue(systemId, out var overridesForSystem) && overridesForSystem.TryGetValue(key, out var s))
			{
				return s;
			}

			if (BaseAxisLookupTable.TryGetValue(key, out var s1))
			{
				return s1;
			}

			// the WHOLE name, not the stripped key: the tables are shared between
			// players on purpose, but a header is not, and a 3DO with two sticks
			// wants P1SH and P2SH rather than SH twice
			return Abbreviate(button);
		}

		/// <summary>
		/// A short header for an axis no table names: the initials of its words,
		/// with anything already short kept whole. "Left Stick X" becomes LSX,
		/// "P1 Gun Screen X" becomes P1GSX, "Left Trigger" becomes LT.
		/// </summary>
		/// <remarks>
		/// The full name used to be the answer, and an axis column is as wide as
		/// its header: four sticks of "Right Stick X" made the input roll wider
		/// than the screen before a single button column was reached. A name
		/// that deserves better than its initials belongs in the table.
		/// </remarks>
		private static string Abbreviate(string name)
		{
			var sb = new System.Text.StringBuilder(6);
			foreach (var word in name.Split(' ', '-', '_'))
			{
				if (word.Length is 0) continue;
				// "P1", "X", "Y", "L", "R" say everything they have to say
				sb.Append(word.Length <= 2 ? word : word.Substring(0, 1));
			}
			return sb.Length is 0 ? name : sb.ToString();
		}

		private static readonly Dictionary<string, char> BaseMnemonicLookupTable = new Dictionary<string, char>
		{
			["Power"] = 'P',
			["Reset"] = 'r',
			["Pause"] = 'p',
			["Rotate"] = 'R',

			["Up"] = 'U',
			["Down"] = 'D',
			["Left"] = 'L',
			["Right"] = 'R',

			["Select"] = 's',
			["SELECT"] = 's',
			["Start"] = 'S',
			["START"] = 'S',
			["Run"] = 'R',
			["RUN"] = 'R',

			["Left Shoulder"] = 'l',
			["Right Shoulder"] = 'r',
			["L"] = 'l',
			["R"] = 'r',

			["L1"] = 'l',
			["R1"] = 'r',

			["L2"] = 'L',
			["R2"] = 'R',

			["L3"] = '<',
			["R3"] = '>',

			["Button"] = 'B',
			["Button 1"] = '1',
			["Button 2"] = '2',
			["Button 3"] = '3',
			["Button 4"] = '4',
			["Button 5"] = '5',
			["Button 6"] = '6',
			["Button 7"] = '7',
			["Button 8"] = '8',
			["Button 9"] = '9',
			["B1"] = '1',
			["B2"] = '2',

			["Trigger"] = '1',
			["Trigger 1"] = '1',
			["Trigger 2"] = '2',

			["Mouse Left"] = 'l',
			["Mouse Right"] = 'r',
			["Mouse Center"] = 'c',
			["Mouse Start"] = 's',

			["Left Button"] = 'l',
			["Middle Button"] = 'm',
			["Right Button"] = 'r',

			["Mode"] = 'M',
			["MODE"] = 'M',
			["Mode 1"] = 'M',
			["Mode 2"] = 'm',

			["Fire"] = 'F',
			["Lightgun Trigger"] = 'T',
			["Lightgun Start"] = 'S',
			["Lightgun B"] = 'B',
			["Lightgun C"] = 'C',
			["Lightgun Offscreen Shot"] = 'O',
			["Microphone"] = 'M',

			["Star"] = '*',
			["Pound"] = '#',

			["X1"] = '1',
			["X2"] = '2',
			["X3"] = '3',
			["X4"] = '4',

			["Y1"] = '1',
			["Y2"] = '2',
			["Y3"] = '3',
			["Y4"] = '4',

			["Triangle"] = 'T',
			["Circle"] = 'O',
			["Cross"] = 'X',
			["Square"] = 'Q',

			["Toggle Left Difficulty"] = 'l',
			["Toggle Right Difficulty"] = 'r',
			["BW"] = 'b',

			["Open"] = 'O',
			["Close"] = 'C',
			["Pedal"] = 'P',

			["Next Disk"] = '>',
			["Previous Disk"] = '<',

			["F1"] = '1',
			["F2"] = '2',
			["F3"] = '3',
			["F4"] = '4',
			["F5"] = '5',
			["F6"] = '6',
			["F7"] = '7',
			["F8"] = '8',
			["F9"] = '9',
			["F10"] = '0',
		};

		private static readonly Dictionary<string, Dictionary<string, char>> SystemOverrides = new Dictionary<string, Dictionary<string, char>>
		{
			// A Dreamcast port takes more than a gamepad, so one player's
			// controls are the union of a controller, a twin stick, a
			// PantherDC, a mouse and a light gun. Six of them would otherwise
			// take a character the d-pad already has: the "D" BUTTON against
			// Down, the second d-pad against the first, and Reload against
			// Right. Held per player, which is what these have to be unique
			// within - the four ports are separate groups in the input roll.
			["DC"] = new()
			{
				["D"] = 'd',            // the fourth face button, not Down
				["Up2"] = '^',          // the twin stick's second d-pad
				["Down2"] = 'v',
				["Left2"] = '<',
				["Right2"] = '>',
				["Reload"] = 'e',       // the light gun's, not Right
				["Mouse Middle"] = 'c', // the base table knows Left and Right
			},

			/* The Duke: A B X Y read from the base table; these are the names
			 * whose first letters collide with them or with each other. */
			["GC"] = new()
			{
				// the triggers, lowercase; Left and Right keep the uppercase pair
				["L"] = 'l',
				["R"] = 'r',
			},
			["XBOX"] = new()
			{
				["Back"] = 's',          // the pad's select, not another B
				["Black"] = 'k',         // blacK; White reads W on its own
				["LStick Click"] = 'l',
				["RStick Click"] = 'r',
			},
			// A Series 60 keypad: the five-way pad and the digits read from the
			// base table, and these four are the ones whose names collide. Both
			// soft keys end in "Soft" and both call keys in "Call", so the
			// last-word fallback hands each pair the same character - which is an
			// ambiguous input log and two columns nobody can tell apart.
			["SYMBIAN"] = new()
			{
				["Left Soft"] = '[',     // the two keys under the screen, as they sit
				["Right Soft"] = ']',
				["Call"] = 'c',          // green
				["End Call"] = 'e',      // red
			},
			[VSystemID.Raw.Panasonic3DO] = new()
			{
				["LT"] = 'l',
				["RT"] = 'r',
				["P"] = 'P',
				["Trigger"] = 'T',
				["Reload"] = 'R',
				["Is Off-Screen"] = 'O',
				["Aux A"] = 'A',
				["Start P1"] = '1',
				["Start P2"] = '2',
				["Coin P1"] = 'C',
				["Coin P2"] = 'c',
				["Service"] = 'S',
				["Fourth Button"] = '4',
				["Next Disc"] = '>',
				["Prev Disc"] = '<',
			},

			/* A DualShock 2 has six shoulder/stick buttons and a d-pad, and the
			   base table gives L2/R2 the same letters as Left/Right. TAStudio
			   heads each column with its MNEMONIC, so two pairs of PS2 columns
			   read identically and there is no way to tell which one you are
			   clicking - reported as buttons landing in the wrong places, which
			   they never did: the wire is exact (the pad gate holds it to a real
			   PlayStation 2 controller tester), it was the LABEL that was
			   ambiguous. Brackets pair with the angles L3/R3 already use.

			   Changing a mnemonic changes the character a new log is written
			   with. Old logs still load: an entry is decoded by POSITION and any
			   character but '.' means pressed, so nothing that was recorded
			   stops replaying. */
			[VSystemID.Raw.A26] = new()
			{
				// the console's own switches. Without these the difficulty
				// toggles fall back to their last word and collide with each
				// other, and the right one collides with Reset besides.
				["Toggle Left Difficulty"] = 'x',
				["Toggle Right Difficulty"] = 'y',
				["Toggle TV Type"] = 'V',
			},


			[VSystemID.Raw.PS3] = new()
			{
				// a DualShock 3: the PS2 pad's shoulders read the same way,
				// and the PS button is the Home key
				["L2"] = '[',
				["R2"] = ']',
				["PS"] = 'H',
			},
			[VSystemID.Raw.PS2] = new()
			{
				["L2"] = '[',
				["R2"] = ']',
				["Analog"] = 'A',   // otherwise '!', which names nothing

				// The two PHYSICAL ports also take the instruments, so one
				// player's controls are the union of a DualShock 2, a Negcon, a
				// guitar and a Pop'n controller - thirty-seven characters that
				// have to differ from each other. The pad's own sixteen are
				// spoken for above and by the base table, which leaves these.
				["Negcon A"] = 'a',
				["Negcon B"] = 'b',
				["Negcon I"] = 'i',
				["Negcon II"] = 'j',

				["Strum Up"] = '^',
				["Strum Down"] = 'v',
				// the frets by colour initial, where the initial was free:
				// r and R are the pad's shoulders, b is the Negcon's
				["Fret Green"] = 'g',
				["Fret Red"] = 'e',
				["Fret Yellow"] = 'y',
				["Fret Blue"] = 'u',
				["Fret Orange"] = 'o',

				// Pop'n Music's nine, numbered the way they sit on the
				// controller: white, yellow, green, blue, RED in the middle,
				// then blue, green, yellow, white back out again
				["Pop White Left"] = '1',
				["Pop Yellow Left"] = '2',
				["Pop Green Left"] = '3',
				["Pop Blue Left"] = '4',
				["Pop Red"] = '5',
				["Pop Blue Right"] = '6',
				["Pop Green Right"] = '7',
				["Pop Yellow Right"] = '8',
				["Pop White Right"] = '9',
			},

			[VSystemID.Raw.NES] = new()
			{
				["FDS Eject"] = 'E',
				["FDS Insert 0"] = '0',
				["FDS Insert 1"] = '1',
				["FDS Insert 2"] = '2',   // a disk has up to four sides
				["FDS Insert 3"] = '3',
				["Insert Coin P1"] = 'c',
				["Insert Coin P2"] = 'C',
				["Service Switch"] = 'w',

				["PP1"] = '1',
				["PP2"] = '2',
				["PP3"] = '3',
				["PP4"] = '4',

				["PP5"] = '5',
				["PP6"] = '6',
				["PP7"] = '7',
				["PP8"] = '8',

				["PP9"] = '9',
				["PP10"] = 'A',
				["PP11"] = 'B',
				["PP12"] = 'C',
				["Click"] = 'C',
				["Touch"] = 'T',
			},
			[VSystemID.Raw.SNES] = new()
			{
				// the Super Scope and the Justifier: two light guns whose
				// buttons the base table has never heard of.
				//
				// These are declared WITHOUT a player prefix, so they share a
				// group with Power and Reset and have to be unique against them
				// too: Scope Pause was 'P' against Power, and Mouse Right took
				// the base table's 'r' against Reset. Two columns a person
				// cannot tell apart, hidden for as long as the uniqueness check
				// skipped this controller for being 108 buttons wide.
				["Scope Trigger"] = 'T',
				["Scope Cursor"] = 'C',
				["Scope Turbo"] = 'u',
				["Scope Pause"] = 'p',
				["Scope Offscreen"] = 'O',
				["Mouse Left"] = 'L',
				["Mouse Right"] = 'R',
				["Justifier Trigger"] = 't',
				["Justifier Start"] = 'S',
				["Justifier Offscreen"] = 'o',

				["Cursor"] = 'c',
				["Turbo"] = 't',
				["Toggle Multitap"] = 't',
				["Offscreen"] = 'o',

				["B0"] = '0',
				["B1"] = '1',
				["B2"] = '2',
				["B3"] = '3',
				["B4"] = '4',
				["B5"] = '5',
				["B6"] = '6',
				["B7"] = '7',
				["B8"] = '8',
				["B9"] = '9',
				["B10"] = 'a',
				["B11"] = 'b',
				["B12"] = 'c',
				["B13"] = 'd',
				["B14"] = 'e',
				["B15"] = 'f',
				["B16"] = 'g',
				["B17"] = 'h',
				["B18"] = 'i',
				["B19"] = 'j',
				["B20"] = 'k',
				["B21"] = 'l',
				["B22"] = 'm',
				["B23"] = 'n',
				["B24"] = 'o',
				["B25"] = 'p',
				["B26"] = 'q',
				["B27"] = 'r',
				["B28"] = 's',
				["B29"] = 't',
				["B30"] = 'u',
				["B31"] = 'v',

				["Extra1"] = '1',
				["Extra2"] = '2',
				["Extra3"] = '3',
				["Extra4"] = '4',

				["Subframe"] = 'F',
			},
			[VSystemID.Raw.TI83] = new()
			{
				["UP"] = 'U',
				["DOWN"] = 'D',
				["LEFT"] = 'L',
				["RIGHT"] = 'R',
				["DOT"] = '`',
				["ON"] = 'O',
				["ENTER"] = '=',
				["PLUS"] = '+',
				["MINUS"] = '_',
				["MULTIPLY"] = '*',
				["DIVIDE"] = '/',
				["CLEAR"] = 'c',
				["EXP"] = '^',
				["DASH"] = '-',
				["PARAOPEN"] = '(',
				["PARACLOSE"] = ')',
				["TAN"] = 'T',
				["VARS"] = 'V',
				["COS"] = 'C',
				["PRGM"] = 'P',
				["STAT"] = 's',
				["MATRIX"] = 'm',
				["X"] = 'X',
				["STO"] = '>',
				["LN"] = 'n',
				["LOG"] = 'L',
				["SQUARED"] = '2',
				["NEG1"] = '1',
				["MATH"] = 'H',
				["ALPHA"] = 'A',
				["GRAPH"] = 'G',
				["TRACE"] = 't',
				["ZOOM"] = 'Z',
				["WINDOW"] = 'W',
				["Y"] = 'Y',
				["2ND"] = '&',
				["MODE"] = 'O',
				["DEL"] = 'D',
				["COMMA"] = ',',
				["SIN"] = 'S',
				["SEND"] = 'N',
			},
			[VSystemID.Raw.C64] = new()
			{
				["L"] = 'L',
				["R"] = 'R',
				["Left Arrow"] = 'l',
				["Plus"] = '+',
				["Minus"] = '-',
				["Pound"] = 'l',
				["Clear/Home"] = 'c',
				["Insert/Delete"] = 'i',
				["Control"] = 'c',
				["At"] = '@',
				["Asterisk"] = '*',
				["Up Arrow"] = 'u',
				["Restore"] = 'r',
				["Run/Stop"] = 's',
				["Lck"] = 'k',
				["Colon"] = ':',
				["Semicolon"] = ';',
				["Equal"] = '=',
				["Return"] = 'e',
				["Commodore"] = 'o',
				["Left Shift"] = 's',
				["Comma"] = ',',
				["Period"] = '>',
				["Slash"] = '/',
				["Right Shift"] = 's',
				["Cursor Up/Down"] = 'u',
				["Cursor Left/Right"] = 'l',
				["Space"] = '_',
			},
			[VSystemID.Raw.ZXSpectrum] = new()
			{
				["Caps Shift"] = '^',
				["Caps Lock"] = 'L',
				["Period"] = '_',
				["Symbol Shift"] = 'v',
				["Semi-Colon"] = ';',
				["Quote"] = '"',
				["Comma"] = ',',
				["True Video"] = 'T',
				["Inv Video"] = 'I',
				["Break"] = 'B',
				["Delete"] = 'D',
				["Graph"] = 'G',
				["Extend Mode"] = 'M',
				["Edit"] = 'E',
				["Play Tape"] = 'P',
				["Stop Tape"] = 'S',
				["RTZ Tape"] = 'r',
				["Record Tape"] = 'R',
				["Insert Next Tape"] = '>',
				["Insert Previous Tape"] = '<',
				["Next Tape Block"] = ']',
				["Prev Tape Block"] = '[',
				["Get Tape Status"] = 'S',
				["Insert Next Disk"] = '}',
				["Insert Previous Disk"] = '{',
				["Get Disk Status"] = 's',
				["Return"] = 'e',
				["Space"] = '-',
				["Up Cursor"] = 'u',
				["Down Cursor"] = 'd',
				["Left Cursor"] = 'l',
				["Right Cursor"] = 'r',
			},
			[VSystemID.Raw.N64] = new()
			{
				["C Up"] = 'u',
				["C Down"] = 'd',
				["C Left"] = 'l',
				["C Right"] = 'r',

				["DPad U"] = 'U',
				["DPad D"] = 'D',
				["DPad L"] = 'L',
				["DPad R"] = 'R',
			},
			[VSystemID.Raw.GBL] = new()
			{
				// gbhawk
				["Toggle Cable"] = 'L',
				["Toggle Cable LC"] = 'L',
				["Toggle Cable CR"] = 'C',
				["Toggle Cable RL"] = 'R',
				["Toggle Cable UD"] = 'U',
				["Toggle Cable LR"] = 'L',
				["Toggle Cable X"] = 'X',
				["Toggle Cable 4x"] = '4',
				// gambatte
				["Toggle Link Connection"] = 'L',
				["Toggle Link Shift"] = 'F',
				["Toggle Link Spacing"] = 'C',
			},
			[VSystemID.Raw.Jaguar] = new()
			{
				["Option"] = 'O',
				["Asterisk"] = '*',
			},
			[VSystemID.Raw.Lynx] = new()
			{
				["Option 1"] = '1',
				["Option 2"] = '2',
			},
			[VSystemID.Raw.NGP] = new()
			{
				["Option"] = 'O',
			},
			[VSystemID.Raw.AppleII] = new()
			{
				["Tab"] = 't' ,
				["Return"] = 'e' ,
				["Escape"] = 'x' ,
				["Delete"] = 'b' ,
				["Space"] = 's' ,
				["Control"] = 'c' ,
				["Shift"] = '^' ,
				["Caps Lock"] = 'C' ,
				["White Apple"] = 'w' ,
				["Black Apple"] = 'b' ,
				["L"] = 'L' ,
				["R"] = 'R',
			},
			[VSystemID.Raw.INTV] = new()
			{
				["Clear"] = 'C' ,
				["Enter"] = 'E' ,
				["Top"] = 'T' ,
				["NNE"] = 'n' ,
				["NE"] = '/' ,
				["ENE"] = 'e' ,
				["ESE"] = 'e' ,
				["SE"] = '\\' ,
				["SSE"] = 's' ,
				["SSW"] = 's' ,
				["SW"] = '/' ,
				["WSW"] = 'w' ,
				["WNW"] = 'w' ,
				["NW"] = '\\' ,
				["NNW"] = 'n',
			},
			[VSystemID.Raw.Coleco] = new()
			{
				["Yellow"] = 'Y',
				["Red"] = 'R',
				["Blue"] = 'B',
				["Purple"] = 'P',
			},
			[VSystemID.Raw.VB] = new()
			{
				["L_Up"] = 'U',
				["L_Down"] = 'D',
				["L_Left"] = 'L',
				["L_Right"] = 'R',
				["R_Up"] = 'u',
				["R_Down"] = 'd',
				["R_Left"] = 'l',
				["R_Right"] = 'r',
				["Battery Voltage: Set Normal"] = '+',
				["Battery Voltage: Set Low"] = '-',
			},
			[VSystemID.Raw.PCFX] = new()
			{
				["I"] = '1',
				["II"] = '2',
				["III"] = '3',
				["IV"] = '4',
				["V"] = '5',
				["VI"] = '6',
				["Mode 1: Set A"] = 'a',
				["Mode 1: Set B"] = 'b',
				["Mode 2: Set A"] = 'A',
				["Mode 2: Set B"] = 'B',
			},
			[VSystemID.Raw.PCE] = new()
			{
				["I"] = '1',
				["II"] = '2',
				["III"] = '3',
				["IV"] = '4',
				["V"] = '5',
				["VI"] = '6',
				["Mode: Set 2-button"] = 'm',
				["Mode: Set 6-button"] = 'M',
			},
			[VSystemID.Raw.PCECD] = new()
			{
				["I"] = '1',
				["II"] = '2',
				["III"] = '3',
				["IV"] = '4',
				["V"] = '5',
				["VI"] = '6',
				["Mode: Set 2-button"] = 'm',
				["Mode: Set 6-button"] = 'M',
			},
			[VSystemID.Raw.N3DS] = new()
			{
				["Debug"] = 'd',
				["GPIO14"] = 'g',
				["ZL"] = 'z',
				["ZR"] = 'z',
				["Touch"] = 'T',
				["Tilt"] = 't',
			},
			[VSystemID.Raw.NDS] = new()
			{
				["LidOpen"] = 'o',
				["LidClose"] = 'c',
				["Microphone"] = 'M',
				["Touch"] = 'T',
			},
			[VSystemID.Raw.O2] = new()
			{
				["PERIOD"] = 'p',
				["SPC"] = 's',
				["YES"] = 'y',
				["NO"] = 'n',
				["CLR"] = 'c',
				["ENT"] = 'e',
			},
			[VSystemID.Raw.Arcade] = new()
			{
				["1 Player Start"] = '1',
				["2 Players Start"] = '2',
				["3 Players Start"] = '3',
				["4 Players Start"] = '4',
				["5 Players Start"] = '5',
				["6 Players Start"] = '6',
				["7 Players Start"] = '7',
				["8 Players Start"] = '8',
				["Advance"] = 'A',
				["Attack"] = 'a',
				["Auto Up / Manual Down"] = 'a',
				["Banknote 1"] = 'b',
				["Block"] = 'B',
				["Block 2"] = 'b',
				["Blue"] = 'B',
				["Board 0 (SW4)"] = '0',
				["Board 1 (SW5)"] = '1',
				["Board 2 (SW6)"] = '2',
				["Board 3 (SW7)"] = '3',
				["Coin 1"] = 'C',
				["Coin 2"] = 'C',
				["Coin 3"] = 'C',
				["Coin 4"] = 'C',
				["Coin 5"] = 'C',
				["Coin 6"] = 'C',
				["Coin 7"] = 'C',
				["Coin 8"] = 'C',
				["Coin 9"] = 'C',
				["Coinblock"] = 'C',
				["Cup Select 1"] = '1',
				["Cup Select 2"] = '2',
				["Door Interlock"] = 'I',
				["Effect"] = 'E',
				["Emergency"] = 'E',
				["Fierce Punch"] = 'F',
				["Fire Down"] = 'd',
				["Fire Left"] = 'l',
				["Fire Right"] = 'r',
				["Fire Up"] = 'u',
				["Foot Pedal"] = 'P',
				["Forward Kick"] = 'f',
				["GEAR 1"] = '1',
				["GEAR 2"] = '2',
				["GEAR 3"] = '3',
				["GEAR 4"] = '4',
				["GEAR N"] = 'N',
				["Green"] = 'G',
				["Gun Trigger"] = 'G',
				["Handle A"] = 'A',
				["Handle B"] = 'B',
				["High Kick"] = 'K',
				["High Punch"] = 'P',
				["High Score Reset"] = 'r',
				["Hyperspace"] = 'H',
				["Jab Punch"] = 'J',
				["Jump"] = 'j',
				["Left Stick/Up"] = '^',
				["Left Stick/Down"] = 'v',
				["Left Stick/Left"] = '<',
				["Left Stick/Right"] = '>',
				["Light"] = 'l',
				["Low Kick"] = 'k',
				["Low Punch"] = 'p',
				["Medium"] = 'm',
				["Move Down"] = 'D',
				["Move Left"] = 'L',
				["Move Right"] = 'R',
				["Move Up"] = 'U',
				["Paddle"] = 'P',
				["Pedal 1"] = '1',
				["Pedal 2"] = '2',
				["Push SW1 (Service)"] = 's',
				["Push SW2 (Test)"] = 'T',
				["Push SW3 (Service)"] = 's',
				["Push SW4 (Test)"] = 'T',
				["Red"] = 'R',
				["Relay"] = 'R',
				["Reverse"] = 'R',
				["Right Stick/Up"] = 'u',
				["Right Stick/Down"] = 'd',
				["Right Stick/Left"] = 'l',
				["Right Stick/Right"] = 'r',
				["Roundhouse Kick"] = 'r',
				["Sensor"] = 'S',
				["Service"] = 'S',
				["Service Mode"] = 'S',
				["Service Mode 2"] = 'S',
				["Service Button"] = 'S',
				["Service 1"] = 's',
				["Service 2"] = 's',
				["Service 3"] = 's',
				["Service 4"] = 's',
				["Short Kick"] = 's',
				["Smart Bomb"] = 'B',
				["Sold Out LED1"] = '1',
				["Sold Out LED2"] = '2',
				["Sold Out LED3"] = '3',
				["Sold Out SW1"] = '1',
				["Sold Out SW2"] = '2',
				["Sold Out SW3"] = '3',
				["Stand"] = 'S',
				["Strong"] = 's',
				["Strong Punch"] = 'S',
				["Test"] = 'T',
				["Thrust"] = 'T',
				["Tilt"] = 'T',
				["Tilt 1"] = 't',
				["Tilt 2"] = 't',
				["Tilt 3"] = 't',
				["Tilt 4"] = 't',
				["Volume Down"] = '-',
				["Volume Up"] = '+',
				["VR1 (Red)"] = 'R',
				["VR2 (Blue)"] = 'B',
				["VR3 (Yellow)"] = 'Y',
				["VR4 (Green)"] = 'G',
			},
			[VSystemID.Raw.SAT] = new()
			{
				["Smpc Reset"] = 's',
				["St-V Test"] = 'T',
				["St-V Service"] = 'V',
				["St-V Pause"] = 'P',
				["Insert Coin"] = 'c',
				["D-Pad Up"] = 'U',
				["D-Pad Down"] = 'D',
				["D-Pad Left"] = 'L',
				["D-Pad Right"] = 'R',
				["Mode: Set Digital(+)"] = '+',
				["Mode: Set Analog(○)"] = 'o',
				["L Gear Shift"] = 'L',
				["R Gear Shift"] = 'R',
				["Offscreen Shot"] = 'O',
			},
			[VSystemID.Raw.PSX] = new()
			{
				["D-Pad Up"] = 'U',
				["D-Pad Down"] = 'D',
				["D-Pad Left"] = 'L',
				["D-Pad Right"] = 'R',
				["Thumbstick Up"] = 'U',
				["Thumbstick Down"] = 'D',
				["Thumbstick Left"] = 'L',
				["Thumbstick Right"] = 'R',
				["□"] = 'Q',
				["△"] = 'T',
				["○"] = 'O',
				["Left Stick, Button"] = '<',
				["Right Stick, Button"] = '>',
				["Left Stick, L-Thumb"] = 'L',
				["Right Stick, L-Thumb"] = 'l',
				["Left Stick, R-Thumb"] = 'R',
				["Right Stick, R-Thumb"] = 'r',
				["Left Stick, Trigger"] = 'T',
				["Right Stick, Trigger"] = 't',
				["Left Stick, Pinky"] = 'P',
				["Right Stick, Pinky"] = 'p',
				["Analog"] = 'M',
				["Offscreen Shot"] = 'o',
				["Open Tray"] = 'o',
				["Close Tray"] = 'c',
			},
			[VSystemID.Raw.TIC80] = new()
			{
				["Mouse Left Click"] = 'l',
				["Mouse Middle Click"] = 'm',
				["Mouse Right Click"] = 'r',
				["Mouse Relative Toggle"] = 't',

				["Minus"] = '-',
				["Equals"] = '=',
				["Left Bracket"] = '[',
				["Right Bracket"] = ']',
				["Backslash"] = '\\',
				["Semicolon"] = ';',
				["Apostrophe"] = '\'',
				["Grave"] = '`',
				["Comma"] = ',',
				["Period"] = 'p',
				["Slash"] = '/',
				["Space"] = 's',
				["Tab"] = 't',
				["Return"] = 'r',
				["Backspace"] = 'b',
				["Delete"] = 'd',
				["Insert"] = 'i',
				["Page Up"] = 'u',
				["Page Down"] = 'v',
				["Home"] = 'h',
				["End"] = 'e',
				["Caps Lock"] = 'l',
				["Control"] = 'c',
				["Shift"] = '^',
				["Alt"] = 'a',
				["Escape"] = 'e',
				["F11"] = '1',
				["F12"] = '2',
			},
			[VSystemID.Raw.Amiga] = new()
			{
				["L"] = 'L',
				["R"] = 'R',
				["NP 0"] = '0',
				["NP 1"] = '1',
				["NP 2"] = '2',
				["NP 3"] = '3',
				["NP 4"] = '4',
				["NP 5"] = '5',
				["NP 6"] = '6',
				["NP 7"] = '7',
				["NP 8"] = '8',
				["NP 9"] = '9',
				["NP Add"] = '+',
				["NP Delete"] = 'd',
				["NP Div"] = '/',
				["NP Enter"] = 'e',
				["NP Left Paren"] = '(',
				["NP Mul"] = '*',
				["NP Right Paren"] = ')',
				["NP Sub"] = '-',
				["Backquote"] = '`',
				["Backslash"] = '\\',
				["Backspace"] = 'b',
				["Caps Lock"] = 'c',
				["Comma"] = ',',
				["Ctrl"] = 'c',
				["Delete"] = 'd',
				["Down"] = 'D',
				["Equal"] = '=',
				["Escape"] = 'e',
				["Help"] = 'h',
				["Left"] = 'L',
				["Left Alt"] = 'a',
				["Left Amiga"] = 'A',
				["Left Bracket"] = '[',
				["Left Shift"] = 's',
				["Less"] = '<',
				["Minus"] = '-',
				["Number Sign"] = '#',
				["Period"] = 'p',
				["Quote"] = '\"',
				["Return"] = 'r',
				["Right"] = 'R',
				["Right Alt"] = 'a',
				["Right Amiga"] = 'A',
				["Right Bracket"] = ']',
				["Right Shift"] = 's',
				["Semicolon"] = ';',
				["Slash"] = '/',
				["Space"] = '_',
				["Tab"] = 't',
				["Up"] = 'U',
				["Joystick Button 1"] = '1',
				["Joystick Button 2"] = '2',
				["Joystick Button 3"] = '3',
				["Joystick Up"] = 'U',
				["Joystick Down"] = 'D',
				["Joystick Left"] = 'L',
				["Joystick Right"] = 'R',
				["CD32 pad Up"] = 'U',
				["CD32 pad Down"] = 'D',
				["CD32 pad Left"] = 'L',
				["CD32 pad Right"] = 'R',
				["CD32 pad Play"] = '>',
				["CD32 pad Rewind"] = '{',
				["CD32 pad Forward"] = '}',
				["CD32 pad Green"] = 'g',
				["CD32 pad Yellow"] = 'y',
				["CD32 pad Red"] = 'r',
				["CD32 pad Blue"] = 'b',
				["Mouse Left Button"] = 'l',
				["Mouse Middle Button"] = 'm',
				["Mouse Right Button"] = 'r',
				["Eject Disk"] = '^',
				["Insert Disk"] = 'v',
				["Next Drive"] = '}',
				["Next Slot"] = '>',
			},
			[VSystemID.Raw.ChannelF] = new()
			{
				["Right"] = 'R',
				["Left"] = 'L',
				["Back"] = 'D',
				["Forward"] = 'U',
				["CCW"] = 'c',
				["CW"] = 'C',
				["Pull"] = 'O',
				["Push"] = 'P',
				["TIME"] = 'T',
				["MODE"] = 'M',
				["HOLD"] = 'H',
				["START"] = 'S',
				["RESET"] = 'r',
			},
			[VSystemID.Raw.Doom] = new()
			{
				["Automap Toggle"] = 'a',
				["Automap +"] = '+',
				["Automap -"] = '-',
				["Automap Full/Zoom"] = 'z',
				["Automap Follow"] = 'f',
				["Automap Up"] = 'u',
				["Automap Down"] = 'd',
				["Automap Right"] = 'r',
				["Automap Left"] = 'l',
				["Automap Grid"] = 'g',
				["Automap Mark"] = 'm',
				["Automap Clear Marks"] = 'c',
				["Backward"] = 'v',
				["Camera Reset"] = 'R',
				["Change Gamma"] = 'G',
				["End Player"] = 'E',
				["Fire"] = 'F',
				["Fly Up"] = 'u',
				["Fly Down"] = 'p',
				["Fly Center"] = 'c',
				["Forward"] = '^',
				["Inventory Left"] = 'L',
				["Inventory Right"] = 'R',
				["Jump"] = 'J',
				["Look Up"] = 'U',
				["Look Down"] = 'D',
				["Look Center"] = 'C',
				["Run"] = 'R',
				["Strafe"] = 'S',
				["Strafe Left"] = '<',
				["Strafe Right"] = '>',
				["Turn Left"] = '{',
				["Turn Right"] = '}',
				["Use"] = 'U',
				["Use Artifact"] = 'A',
				["Weapon Select 1"] = '1',
				["Weapon Select 2"] = '2',
				["Weapon Select 3"] = '3',
				["Weapon Select 4"] = '4',
				["Weapon Select 5"] = '5',
				["Weapon Select 6"] = '6',
				["Weapon Select 7"] = '7',
			},
			[VSystemID.Raw.DOS] = new()
			{
				["L"] = 'L',
				["R"] = 'R',
				["Joystick Button 1"] = '1',
				["Joystick Button 2"] = '2',
				["Joystick Up"] = 'U',
				["Joystick Down"] = 'D',
				["Joystick Left"] = 'L',
				["Joystick Right"] = 'R',
				["Mouse Left Button"] = 'l',
				["Mouse Middle Button"] = 'm',
				["Mouse Right Button"] = 'r',
				["Previous Floppy Disk"] = '<',
				["Next Floppy Disk"] = '>',
				["Swap Floppy Disk"] = 'v',
				["Previous CDROM"] = '{',
				["Next CDROM"] = '}',
				["Swap CDROM"] = 'w',
				["F1"] = '1',
				["F2"] = '2',
				["F3"] = '3',
				["F4"] = '4',
				["F5"] = '5',
				["F6"] = '6',
				["F7"] = '7',
				["F8"] = '8',
				["F9"] = '9',
				["F10"] = '0',
				["F11"] = '1',
				["F12"] = '2',
				["Escape"] = 'e',
				["Tab"] = 't',
				["Backspace"] = 'b',
				["Enter"] = 'e',
				["Space"] = 's',
				["LeftAlt"] = 'a',
				["RightAlt"] = 'a',
				["LeftCtrl"] = 'c',
				["RightCtrl"] = 'c',
				["LeftShift"] = 's',
				["RightShift"] = 's',
				["CapsLock"] = 'C',
				["ScrollLock"] = 'S',
				["NumLock"] = 'N',
				["Grave"] = '`',
				["Minus"] = '-',
				["Equals"] = '=',
				["Backslash"] = 'b',
				["LeftBracket"] = '[',
				["RightBracket"] = ']',
				["Semicolon"] = ';',
				["Quote"] = '\'',
				["Period"] = 'p', // because '.' represents no input
				["Comma"] = ',',
				["Slash"] = '/',
				["ExtraLtGt"] = '>',
				["PrintScreen"] = 'p',
				["Pause"] = 'P',
				["Insert"] = 'i',
				["Home"] = 'h',
				["Pageup"] = 'p',
				["Delete"] = 'd',
				["End"] = 'e',
				["Pagedown"] = 'p',
				["Left"] = '<',
				["Up"] = '^',
				["Down"] = 'v',
				["Right"] = '>',
				["KeyPad1"] = '1',
				["KeyPad2"] = '2',
				["KeyPad3"] = '3',
				["KeyPad4"] = '4',
				["KeyPad5"] = '5',
				["KeyPad6"] = '6',
				["KeyPad7"] = '7',
				["KeyPad8"] = '8',
				["KeyPad9"] = '9',
				["KeyPad0"] = '0',
				["KeyPadDivide"] = '/',
				["KeyPadMultiply"] = '*',
				["KeyPadMinus"] = '-',
				["KeyPadPlus"] = '+',   // was keyPadPlus, which no key is called, so it came out '!'
				["KeyPadEnter"] = 'e',
				["KeyPadPeriod"] = 'p',
			},
		};

		private static readonly Dictionary<string, string> BaseAxisLookupTable = new Dictionary<string, string>
		{
			["Zapper X"] = "zapX",
			["Zapper Y"] = "zapY",
			["Paddle"] = "Pad",
			["Pen"] = "Pen",
			["Mouse X"] = "mX",
			["Mouse Y"] = "mY",
			["Lightgun X"] = "lX",
			["Lightgun Y"] = "lY",
			["X Axis"] = "aX",
			["Y Axis"] = "aY",
			["LStick X"] = "lsX",
			["LStick Y"] = "lsY",
			["RStick X"] = "rsX",
			["RStick Y"] = "rsY",
			["Disc Select"] = "Disc",
			["Disk Index"] = "Disk",
		};

		private static readonly Dictionary<string, Dictionary<string, string>> AxisSystemOverrides = new Dictionary<string, Dictionary<string, string>>
		{
			// A PSP has one player, so the "P1 " its core declares is noise in a
			// column two characters wide. The lookup has already stripped it by
			// the time this table is consulted.
			[VSystemID.Raw.PSP] = new()
			{
				["L-Stick X"] = "LSX",
				["L-Stick Y"] = "LSY",
			},

			// Eight slots of four axes, and the two physical ports four more
			// for the instruments. "Tilt" and "Twist" both abbreviate to T, so
			// the whole set is named rather than half of it. No player prefix -
			// the input roll keeps each slot in its own group.
			[VSystemID.Raw.PS2] = new()
			{
				["Left Stick X"] = "LSX",
				["Left Stick Y"] = "LSY",
				["Right Stick X"] = "RSX",
				["Right Stick Y"] = "RSY",
				["Dial"] = "Dial",     // the Jogcon's
				["Twist"] = "Twst",    // the Negcon's
				["Whammy"] = "Whmy",   // the guitar's
				["Tilt"] = "Tilt",
			},

			// Eleven axes per port, four ports: forty-four columns, and the
			// abbreviator would head some of them "P1MW" and let others fall
			// through to the shared table's "mX". Named here so one player's
			// eleven read as a set. The player prefix is not needed - the input
			// roll keeps each port in its own group.
			["DC"] = new()
			{
				["Stick X"] = "SX",
				["Stick Y"] = "SY",
				["Left Trigger"] = "LT",
				["Right Trigger"] = "RT",
				["Stick 2 X"] = "S2X",
				["Stick 2 Y"] = "S2Y",
				["Mouse X"] = "MX",
				["Mouse Y"] = "MY",
				["Mouse Wheel"] = "MW",
				["Gun X"] = "GX",
				["Gun Y"] = "GY",
			},

			[VSystemID.Raw.Panasonic3DO] = new()
			{
				["Flight Stick Horizontal Axis"] = "fsX",
				["Flight Stick Vertical Axis"] = "fsY",
				["Flight Stick Altitude Axis"] = "fsZ",
				["Light Gun Screen X"] = "lgX",
				["Light Gun Screen Y"] = "lgY",
				["Trackball X"] = "tX",
				["Trackball Y"] = "tY",
			},
			[VSystemID.Raw.A78] = new()
			{
				["VPos"] = "X",
				["HPos"] = "Y",
			},
			[VSystemID.Raw.PSX] = new()
			{
				["Left Stick Left / Right"] = "lsX",
				["Left Stick Up / Down"] = "lsY",
				["Right Stick Left / Right"] = "rsX",
				["Right Stick Up / Down"] = "rsY",
				["Left Stick, Left / Right"] = "lsX",
				["Left Stick, Up / Down"] = "lsZ",
				["Right Stick, Left / Right"] = "rsX",
				["Right Stick, Up / Down"] = "rsZ",
				["Motion Left / Right"] = "mX",
				["Motion Up / Down"] = "mY",
				["Twist Ccwise / Cwise"] = "Twist",
			},
			[VSystemID.Raw.TIC80] = new()
			{
				["Mouse Position X"] = "mpX",
				["Mouse Position Y"] = "mpY",
				["Mouse Scroll X"] = "msX",
				["Mouse Scroll Y"] = "msY",
			},
			[VSystemID.Raw.Doom] = new()
			{
				["Camera Mode"] = "cM",
				["Camera Run Speed"] = "cRun",
				["Camera Strafe Speed"] = "cStr",
				["Camera Turn Speed"] = "cTurn",
				["Camera Fly"] = "cFly",
				["Mouse Run"] = "mRun",
				["Mouse Turn"] = "mTurn",
				["Run Speed"] = "Run",
				["Strafe Speed"] = "Str",
				["Turn Speed"] = "Turn",
				["Turn Speed Frac."] = "turn",
				["Use Artifact"] = "Arti",
				["Weapon Select"] = "W",
			},
			[VSystemID.Raw.DOS] = new()
			{
				["Mouse Position X"] = "mpX",
				["Mouse Position Y"] = "mpY",
				["Mouse Speed X"] = "msX",
				["Mouse Speed Y"] = "msY",
			},
			[VSystemID.Raw.Arcade] = new()
			{
				["Pedal 1"] = "pedal1",
			},
		};

		static MnemonicLookup()
		{
			const string ERR_FMT_STR = $"{nameof(MnemonicLookup)}.{{1}}[\"{{0}}\"] must not be '.' as that indicates unpressed buttons";
			foreach (var (k, v) in BaseMnemonicLookupTable)
			{
				Debug.Assert(v is not '.', ERR_FMT_STR, k, nameof(BaseMnemonicLookupTable));
			}
			foreach (var (sysID, dict) in SystemOverrides) foreach (var (k, v) in dict)
			{
				Debug.Assert(v is not '.', ERR_FMT_STR, k, $"{nameof(SystemOverrides)}[{sysID}]");
			}
		}
	}
}
