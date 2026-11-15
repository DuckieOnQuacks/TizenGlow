namespace TizenGlow.WebSocket.DataTypes
{
    public enum Event
    {
        SetConfig,
        ReadConfig,
        ReadConfigResult
    }

    public class BasicEvent
    {
        public Event Event { get; set; }
    }

    public class SetConfigEvent : BasicEvent
    {
        public string key { get; set; }
        public string value { get; set; }
    }

    public class ReadConfigEvent : BasicEvent
    {
        public string key { get; set; }
    }

    public class ReadConfigResultEvent : BasicEvent
    {
        public ReadConfigResultEvent(bool error, string key, object value)
        {
            this.Event = Event.ReadConfigResult;
            this.error = error;
            this.value = value;
            this.key = key;
        }

        public bool error { get; set; }
        public string key { get; set; }
        public object value { get; set; }
    }
}
