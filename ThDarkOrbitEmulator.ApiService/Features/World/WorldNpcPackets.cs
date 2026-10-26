namespace ThDarkOrbitEmulator.ApiService.Features.World;

public static class WorldNpcPackets
{
    public static byte[] Create(WorldNpc npc) => WorldPayload.Encode(w =>
    {
        WorldPayload.WriteInt32(w, npc.Id);
        WorldPayload.WriteString(w, npc.Kind);
        WorldPayload.WriteString(w, npc.ShipId);
        WorldPayload.WriteInt32(w, npc.X);
        WorldPayload.WriteInt32(w, npc.Y);
        WorldPayload.WriteInt32(w, npc.Hp);
        WorldPayload.WriteInt32(w, npc.MaxHp);
        WorldPayload.WriteInt32(w, npc.Shield);
        WorldPayload.WriteInt32(w, npc.MaxShield);
    });

    public static byte[] Health(WorldNpc npc) => WorldPayload.Encode(w =>
    {
        WorldPayload.WriteInt32(w, npc.Id);
        WorldPayload.WriteInt32(w, npc.Hp);
        WorldPayload.WriteInt32(w, npc.MaxHp);
        WorldPayload.WriteInt32(w, npc.Shield);
        WorldPayload.WriteInt32(w, npc.MaxShield);
    });
}
