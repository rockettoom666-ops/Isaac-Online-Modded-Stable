"""Behavior tests with Lua 5.4 mocks. Optional fixture path contains patched mod copies.
Run: python -m pip install lupa==2.8 && python tests/test_lua.py [fixture-directory]
No Isaac install or game execution is required. Mocks do not prove network compatibility.
"""
import sys
import unittest
from pathlib import Path
from lupa.lua54 import LuaRuntime

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = Path(sys.argv.pop()) if len(sys.argv) > 1 else None


def function(text, signature):
    start = text.index(signature)
    return text[start:text.index('\nend', start) + 4]


class MusicTests(unittest.TestCase):
    def test_music_never_uses_gameplay_rng_on_room_or_restart(self):
        lua = LuaRuntime()
        lua.execute('''
            Random = function() error("shared gameplay RNG consumed") end
            frame = 0
            Game = function() return { GetFrameCount = function() return frame end } end
            RNG = function() return {
                SetSeed = function(self, seed) assert(seed > 0 and seed <= 4294967295) end,
                RandomInt = function(self, max) return max - 1 end
            } end
            Isaac = {GetMusicIdByName = function() return 1 end}
            ModCallbacks = {MC_POST_NEW_ROOM = 1}
            RegisterMod = function() return {AddCallback = function(self, id, callback) roomCallback = callback end} end
        ''')
        text = (ROOT / 'Afterbirth Music+++/main.lua').read_text()
        prefix = text[:text.index('local function init()')]
        lua.execute(prefix)
        for frame in (1, 2, 50, 1000, 0, 1, 2, 50, 0, 1):
            lua.globals().frame = frame
            lua.execute('roomCallback()')

    def test_music_player_indices(self):
        text = (ROOT / 'Afterbirth Music+++/main.lua').read_text()
        start = text.index('        local hasTmtrainer = false')
        end = text.index('\n        if hasTmtrainer then', start)
        lua = LuaRuntime()
        lua.execute('''
          Game = function() return {GetNumPlayers = function() return 4 end} end
          seen = {}
          Isaac = {GetPlayer = function(i)
            assert(i >= 0 and i < 4); seen[i] = true
            return {HasCollectible = function() return false end}
          end}
          CollectibleType = {COLLECTIBLE_TMTRAINER = 1}
        ''')
        lua.execute(text[start:end])
        lua.execute('for i = 0, 3 do assert(seen[i]) end')


@unittest.skipUnless(FIXTURES, 'provide patched copies to test installed mod variants')
class CompatibilityTests(unittest.TestCase):
    def source(self, relative):
        return (FIXTURES / relative).read_text(encoding='utf-8-sig')

    def lua(self):
        lua = LuaRuntime()
        lua.execute('''
            count = 2
            game = {GetNumPlayers = function() return count end, GetFrameCount = function() return frame end}
            Game = function() return game end
            frame = 0
            ModCallbacks = {MC_POST_UPDATE=1, MC_POST_GAME_STARTED=2, MC_PRE_GAME_EXIT=3, MC_POST_GAME_END=4, MC_INPUT_ACTION=5}
        ''')
        return lua

    def test_all_patched_files_parse(self):
        lua = self.lua()
        for path in FIXTURES.rglob('*.lua'):
            result = lua.eval('function(text) local f, err = load(text); return f ~= nil, err end')(path.read_text(encoding='utf-8-sig'))
            self.assertTrue(result[0], str(path) + ': ' + str(result[1]))

    def test_restart_discards_old_player_callbacks(self):
        lua = self.lua()
        scheduler = lua.execute(self.source('3034946842/scheduler.lua'))
        lua.globals().scheduler = scheduler
        lua.execute('''
            callbacks = {}
            scheduler.Init({AddCallback=function(self,id,callback) callbacks[id]=callback end})
            called = 0
            scheduler.Schedule(2, function() called=called+1 end)
            callbacks[ModCallbacks.MC_POST_GAME_STARTED]()
            frame = 100
            callbacks[ModCallbacks.MC_POST_UPDATE]()
            assert(called == 0)
            scheduler.Schedule(2, function() called=called+1 end)
            frame = 102
            callbacks[ModCallbacks.MC_POST_UPDATE]()
            assert(called == 1)
        ''')

    def test_emotes_do_not_consume_local_input_in_coop(self):
        lua = self.lua()
        lua.execute('EmoteBinds = {}; saveManager = {GetSaveData=function() error("read local binds") end}')
        lua.execute(function(self.source('3034946842/main.lua'), 'function EmoteBinds:KeybindManager(player)'))
        lua.execute('EmoteBinds:KeybindManager({})')
        lua.execute('count=1; saveManager.GetSaveData=function() return {ControllerBinds={},KeyboardBinds={}} end; EmoteBinds:KeybindManager({})')

    def test_eid_search_closes_and_never_blocks_coop_input(self):
        lua = self.lua()
        lua.execute('''
            removed=0; added=0; searchInputEnabled=true
            EID={Config={BagOfCraftingDisplayRecipesMode="Recipe List"}, BoCSLockMode=0,
                 BoCSGetLocked=function() return true end,
                 RemoveCallback=function() removed=removed+1 end,
                 AddCallback=function() added=added+1 end}
        ''')
        source = self.source('836319872/features/eid_bagofcrafting_search.lua')
        lua.execute(function(source, 'function EID:BoCSSetSearchInputEnabled(newState, force)'))
        lua.execute(function(source, 'function EID:BoCSBlockInputAction(_, inputHook, _)'))
        lua.execute('''
            EID:BoCSSetSearchInputEnabled(true)
            assert(searchInputEnabled == false and removed == 1 and added == 0)
            assert(EID:BoCSBlockInputAction(nil, 1, nil) == nil)
            count=1; EID:BoCSSetSearchInputEnabled(true, true)
            assert(added == 1 and searchInputEnabled)
        ''')

    def test_eid_reminder_does_not_change_gameplay_cooldown(self):
        source = self.source('836319872/features/eid_holdmapdesc.lua')
        line = next(line for line in source.splitlines() if 'ControlsCooldown = 2' in line)
        lua = self.lua()
        lua.execute('EID={Config={ItemReminderDisableInputs=true},holdTabPlayer={ControlsCooldown=0}}')
        lua.execute(line)
        lua.execute('assert(EID.holdTabPlayer.ControlsCooldown == 0); count=1')
        lua.execute(line)
        lua.execute('assert(EID.holdTabPlayer.ControlsCooldown == 2)')

    def test_eid_recipe_browsing_preserves_coop_movement(self):
        source = self.source('836319872/features/eid_bagofcrafting.lua')
        line = next(line for line in source.splitlines() if 'ControlsCooldown = 2' in line)
        lua = self.lua()
        lua.execute('EID={bagPlayer={ControlsCooldown=0}}')
        for count in (2, 4, 1):
            lua.globals().count = count
            lua.execute('EID.bagPlayer.ControlsCooldown = 0')
            lua.execute(line)
            self.assertEqual(lua.eval('EID.bagPlayer.ControlsCooldown'), 2 if count == 1 else 0)

    def test_cuerlib_restart_cannot_identify_player_from_old_inputs(self):
        lua = self.lua()
        lua.execute('''
            ModCallbacks.MC_POST_PLAYER_INIT=6
            ModCallbacks.MC_POST_PLAYER_UPDATE=7
            CallbackPriority={IMPORTANT=1}
            callbacks={}; temp={}; players={}
            for i=0,1 do
                local p={Id=i, Variant=0, ControllerIndex=i+1}
                p.GetMainTwin=function(self) return self end
                p.GetPlayerType=function() return 0 end
                p.GetName=function() return "test" end
                players[i]=p
            end
            game.GetPlayer=function(self,i) return players[i] end
            GetPtrHash=function(p) return p.Id end
            LIB={Players={GetPlayerId=function(p) return p.Id end}}
            LIB.NewClass=function() return {
                AddCallback=function(self,id,fn) callbacks[id]=fn end,
                AddPriorityCallback=function(self,id,priority,fn) callbacks[id]=fn end
            } end
            LIB.GetTempGlobalLibData=function(self,key,field) return temp[field] end
            LIB.SetTempGlobalLibData=function(self,value,key,field) temp[field]=value end
            ButtonAction={ACTION_LEFT=0,ACTION_MAP=0}
            keyboard=true
            Input={
                GetActionValue=function(action,controller)
                    return ((controller==0 and keyboard) or controller==2) and 1 or 0
                end,
                IsActionPressed=function(action,controller)
                    return (controller==0 and keyboard) or controller==2
                end,
                IsActionTriggered=function() return false end
            }
            function startRun()
                frame=0; temp={}
                for i=0,1 do callbacks[6](nil,players[i]) end
                for i=0,1 do callbacks[7](nil,players[i]) end
                callbacks[2](nil,false)
            end
        ''')
        lua.globals().netcoop = lua.execute(self.source('2900345009/cuerlib/class/netcoop.lua'))
        lua.execute('''
            startRun()
            for i=1,20 do frame=i; netcoop:PostUpdate() end
            assert(netcoop.GetLocalPlayerIndex()==1)
            keyboard=false
            startRun()
            for i=1,5 do frame=i; netcoop:PostUpdate(); assert(netcoop.GetLocalPlayerIndex()==-1) end
            keyboard=true
            for i=1,20 do frame=i; netcoop:PostUpdate() end
            assert(netcoop.GetLocalPlayerIndex()==1)
        ''')

    def test_mcm_cannot_open_block_or_force_input_in_coop(self):
        source = self.source('3701683951/scripts/modconfig.lua')
        lua = self.lua()
        lua.execute('MCM={IsVisible=true}')
        for signature in ('function MCM.OpenConfigMenu()', 'function MCM.PostRender()',
                          'function MCM.InputAction(_, entity, inputHook, buttonAction)',
                          'function MCM.HandleForceActionPressed(_, entity, inputHook, buttonAction)'):
            lua.execute(function(source, signature))
        lua.execute('''
            MCM.PostRender(); assert(MCM.IsVisible == false)
            MCM.OpenConfigMenu(); assert(MCM.IsVisible == false)
            assert(MCM.InputAction(nil, {}, 1, 1) == nil)
            assert(MCM.HandleForceActionPressed(nil, {}, 1, 1) == nil)
            count=1; MCM.RoomIsSafe=function() return false end
            beep=false; SFXManager=function() return {Play=function() beep=true end} end
            SoundEffect={SOUND_BOSS2INTRO_ERRORBUZZ=1}
            MCM.OpenConfigMenu(); assert(beep)
        ''')

    def test_specialist_player_indices(self):
        lua = self.lua()
        lua.execute('''
            Epic={}; danceCostumes={}; PlayerType={PLAYER_CAIN_B=23}; seen={}
            game.GetPlayer=function(self, i)
                assert(i >= 0 and i < count); seen[i]=true
                return {GetPlayerType=function() return 0 end}
            end
        ''')
        lua.execute(function(self.source('2575911103/main.lua'), 'function Epic:DoCostume(apply)'))
        lua.execute('Epic:DoCostume(false); assert(seen[0] and seen[1])')

    def test_cuerlib_converts_zero_based_index_before_table_operations(self):
        source = self.source('2900345009/cuerlib/class/netcoop.lua')
        self.assertIn('table.remove(infos, index + 1)', source)
        self.assertIn('table.insert(infos, 1, info)', source)
        lua = self.lua()
        lua.execute('''
            infos = {{Id = "local"}, {Id = "remote"}}
            index = 0
            local info = table.remove(infos, index + 1)
            table.insert(infos, 1, info)
            assert(infos[1].Id == "local" and infos[2].Id == "remote")
        ''')


if __name__ == '__main__':
    unittest.main()
