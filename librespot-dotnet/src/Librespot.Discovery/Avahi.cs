// Port of discovery/src/avahi.rs: NOT PORTED (Linux-only).
//
// avahi.rs holds the zbus D-Bus proxies (`ServerProxy`, `EntryGroupProxy`, `EntryGroupState`) that lib.rs'
// `avahi_task` / `launch_avahi` use to publish the service through avahi-daemon. Avahi only exists on Linux, and the
// built-in responder in Mdns/ (backend "libmdns") covers every platform, so the backend is omitted: its entry in
// DnsSdBackends.All stays null ("known but not compiled"), exactly like an upstream build without `with-avahi`, where
// `launch_avahi` does not exist either. This file therefore contains no code; DnsSdBackends.Find("avahi") reports
// "librespot built without 'avahi' support" (Unavailable).
