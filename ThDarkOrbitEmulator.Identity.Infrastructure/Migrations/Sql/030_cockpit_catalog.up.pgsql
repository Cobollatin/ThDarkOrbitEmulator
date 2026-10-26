CREATE TABLE identity.client_languages (
    code varchar(12) PRIMARY KEY,
    name text NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    is_default boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX client_languages_one_default ON identity.client_languages(is_default) WHERE is_default;
INSERT INTO identity.client_languages(code,name,enabled,is_default) VALUES
('es','Español',true,true),('en','English UK',true,false),('de','Deutsch',true,false),
('fr','Français',true,false),('it','Italiano',true,false),('pl','Polski',true,false),
('tr','Türkçe',true,false),('pt','Português',true,false);

CREATE TABLE identity.client_servers (
    code varchar(32) PRIMARY KEY,
    instance_name text NOT NULL,
    host text NOT NULL,
    map_id integer NOT NULL CHECK (map_id > 0),
    status varchar(16) NOT NULL CHECK (status IN ('online','maintenance','offline')),
    enabled boolean NOT NULL DEFAULT true,
    is_default boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX client_servers_one_default ON identity.client_servers(is_default) WHERE is_default;
INSERT INTO identity.client_servers VALUES ('local','Local emulator','127.0.0.1',1,'online',true,true);

CREATE TABLE identity.client_locale_overrides (
    language_code varchar(12) NOT NULL REFERENCES identity.client_languages(code),
    category varchar(80) NOT NULL,
    key varchar(160) NOT NULL,
    value text NOT NULL,
    PRIMARY KEY(language_code,category,key)
);

CREATE TABLE economy.ship_catalog (
    ship_id varchar(80) PRIMARY KEY,
    name text NOT NULL,
    model_id integer NOT NULL,
    icon_path text NOT NULL,
    enabled boolean NOT NULL DEFAULT true
);
INSERT INTO economy.ship_catalog VALUES
('ship_pusat-plus','Pusat Plus',534,'/do_img/global/items/ship/pusat-plus_100x100.png',true),
('ship_phoenix','Phoenix',1,'/do_img/global/items/ship/phoenix_100x100.png',true);
CREATE TABLE economy.account_hangar_ships (
    account_id uuid NOT NULL REFERENCES identity.accounts(id) ON DELETE CASCADE,
    slot_number integer NOT NULL CHECK (slot_number BETWEEN 1 AND 2),
    ship_id varchar(80) NOT NULL REFERENCES economy.ship_catalog(ship_id),
    hangar_id varchar(6) NOT NULL,
    PRIMARY KEY(account_id,slot_number)
);
