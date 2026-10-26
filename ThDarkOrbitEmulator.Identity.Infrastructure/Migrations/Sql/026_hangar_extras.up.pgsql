INSERT INTO economy.items (id, name, type, asset_key)
VALUES ('extra_cpu', 'Slot CPU', 'extra', 'extra_cpu')
ON CONFLICT (id) DO NOTHING;

INSERT INTO economy.listings (id, item_id, prices)
VALUES ('extra_cpu', 'extra_cpu', '{"credits":750}')
ON CONFLICT (id) DO NOTHING;
