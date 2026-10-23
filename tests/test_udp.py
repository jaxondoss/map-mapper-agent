import threading

from pythonosc.dispatcher import Dispatcher
from pythonosc.osc_server import ThreadingOSCUDPServer

from madmapper_agent import actions
from madmapper_agent.osc import OscSender


def test_real_udp_round_trip(book):
    received = []
    got = threading.Event()

    def handler(address, *args):
        received.append((address, list(args)))
        got.set()

    dispatcher = Dispatcher()
    dispatcher.set_default_handler(handler)
    server = ThreadingOSCUDPServer(("127.0.0.1", 0), dispatcher)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        osc = OscSender("127.0.0.1", port)
        actions.set_opacity(osc, book, "front wall", 0.5)
        assert got.wait(2), "no OSC message received"
    finally:
        server.shutdown()
        server.server_close()

    assert received == [("/surfaces/FrontWall/opacity", [0.5])]
