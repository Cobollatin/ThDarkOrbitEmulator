UPDATE economy.account_preferences SET language = 'es', revision = revision + 1
WHERE language IN ('en_US','sv','ru','fi','da','no','cs','sk','pt_BR','ro','el','bg','hu','es_CO','es_VE','es_CL','es_AR','es_PE','ja');
DELETE FROM identity.client_locale_overrides
WHERE language_code IN ('en_US','sv','ru','fi','da','no','cs','sk','pt_BR','ro','el','bg','hu','es_CO','es_VE','es_CL','es_AR','es_PE','ja');
DELETE FROM identity.client_languages
WHERE code IN ('en_US','sv','ru','fi','da','no','cs','sk','pt_BR','ro','el','bg','hu','es_CO','es_VE','es_CL','es_AR','es_PE','ja');
