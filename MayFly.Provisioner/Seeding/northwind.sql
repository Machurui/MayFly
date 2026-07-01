CREATE TABLE categories (id serial PRIMARY KEY, name text NOT NULL);
CREATE TABLE products (id serial PRIMARY KEY, name text NOT NULL, category_id int REFERENCES categories(id), price numeric(10,2));
CREATE TABLE customers (id serial PRIMARY KEY, company text NOT NULL, country text);
CREATE TABLE orders (id serial PRIMARY KEY, customer_id int REFERENCES customers(id), ordered_at date, total numeric(10,2));
INSERT INTO categories(name) VALUES ('Beverages'),('Condiments'),('Confections');
INSERT INTO products(name,category_id,price) VALUES
  ('Chai',1,18.00),('Chang',1,19.00),('Aniseed Syrup',2,10.00),('Chocolade',3,12.75);
INSERT INTO customers(company,country) VALUES ('Alfreds',' Germany'),('Around the Horn','UK'),('Bottom-Dollar','Canada');
INSERT INTO orders(customer_id,ordered_at,total) VALUES (1,'2026-01-10',54.00),(2,'2026-02-02',38.00);
