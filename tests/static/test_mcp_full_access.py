"""Full access settles tool consent, while actual MCP forms still reach the owner."""
from types import SimpleNamespace
import pytest
from tests.static.test_chat_gateway import _chat


def request(settings, kind='mcp_tool_call', mode='form'):
    module = _chat()
    replies, asks = [], []
    agent = object.__new__(module.CodexAgent)
    agent.settings = settings
    agent.approvals = {}
    agent.proc = SimpleNamespace(reply=lambda *a, **kw: replies.append((a,kw)))
    agent.s = SimpleNamespace(ask=lambda *a, **kw: asks.append(a))
    agent.request({'id':7,'method':'mcpServer/elicitation/request','params':{
        'serverName':'verticals_desktop','mode':mode,
        '_meta':{'codex_approval_kind':kind},
        'message':'A tool approval' if kind else 'Enter a value',
        'requestedSchema':{'type':'object','properties':{}},
    }})
    return replies, asks


@pytest.mark.parametrize('sources', [None,'connected'])
def test_full_access_accepts_tool_consent_without_waiting_for_the_owner(sources):
    replies, asks = request({'permission':'full-access','sources':sources})
    assert not asks
    assert replies == [((7, {'action':'accept','content':{},'_meta':None}),{})]


@pytest.mark.parametrize('settings,kind,mode', [
    ({'permission':'default'},'mcp_tool_call','form'),
    ({'permission':'full-access','role':'checker'},'mcp_tool_call','form'),
    ({'permission':'full-access'},None,'form'),
    ({'permission':'full-access'},'mcp_tool_call','url'),
])
def test_restricted_tools_and_real_forms_still_require_a_decision(settings,kind,mode):
    replies, asks = request(settings,kind,mode)
    assert not replies
    assert len(asks)==1
