-- Unity client language codes beyond the original eight (DarkOrbit.Settings static constructor).
INSERT INTO identity.client_languages(code,name,enabled,is_default) VALUES
('en_US','English US',true,false),('sv','Svenska',true,false),('ru','Русский',true,false),
('fi','Suomi',true,false),('da','Dansk',true,false),('no','Norsk',true,false),
('cs','Čeština',true,false),('sk','Slovenčina',true,false),('pt_BR','Português BR',true,false),
('ro','Română',true,false),('el','Ελληνικά',true,false),('bg','Български',true,false),
('hu','Magyar',true,false),('es_CO','Español (Colombia)',true,false),('es_VE','Español (Venezuela)',true,false),
('es_CL','Español (Chile)',true,false),('es_AR','Español (Argentina)',true,false),('es_PE','Español (Perú)',true,false),
('ja','日本語',true,false)
ON CONFLICT (code) DO NOTHING;
